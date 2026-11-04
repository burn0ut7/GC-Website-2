using GC.Infrastructure;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;
using Npgsql;

namespace GC.Web;

public static class DataExplorer
{
    public static void Map(WebApplication app, RouteGroupBuilder admin)
    {
        app.MapGet("/admin/data", () => Results.File(Path.Combine(AppContext.BaseDirectory,"PrivatePages","data.html"),"text/html"))
            .WithMetadata(new Identity.ProtectedPage()).RequireAuthorization(SiteNavigation.AdminPolicy);
        var routes = admin.MapGroup("/explorer");
        routes.AddEndpointFilter(async (c, next) =>
        {
            c.HttpContext.Response.Headers.CacheControl = "no-store";
            if (c.HttpContext.RequestServices.GetService<NpgsqlDataSource>() is null) return Results.StatusCode(503);
            if (HttpMethods.IsPost(c.HttpContext.Request.Method) || HttpMethods.IsDelete(c.HttpContext.Request.Method))
            {
                try { await c.HttpContext.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(c.HttpContext); }
                catch (AntiforgeryValidationException) { return Results.BadRequest(new { error="Reload the page before making changes." }); }
            }
            try { return await next(c); }
            catch (Exception e) when (LobbyBatch.IsInvalid(e)) { return Results.BadRequest(new { error="Invalid data or request. Use an original Lobby2 batch file." }); }
            catch (PostgresException e) when (e.SqlState == "55P03") { return Results.Conflict(new { error="Data is being processed. Try again shortly." }); }
            catch (IOException) { return Results.Json(new { error="The original archive is unavailable." },statusCode:503); }
        });
        routes.MapGet("/servers", async (HttpContext h) =>
        {
            var servers=new Dictionary<Guid,string>();
            if(Guid.TryParse(app.Configuration["Ingest:ServerId"],out var configured)&&configured!=Guid.Empty)servers[configured]="Configured game server";
            await using var q=h.RequestServices.GetRequiredService<NpgsqlDataSource>().CreateCommand("SELECT DISTINCT server_id FROM gc.ingest_receipts ORDER BY server_id LIMIT 1000");
            await using var r=await q.ExecuteReaderAsync(h.RequestAborted);
            while(await r.ReadAsync(h.RequestAborted)){var id=r.GetGuid(0);servers.TryAdd(id,"Server "+id.ToString()[..8]);}
            return Results.Ok(servers.Select(s=>new{id=s.Key,name=s.Value}));
        });
        routes.MapGet("", async (HttpContext h, string stream, string? search, int? offset) =>
        {
            if (!Intake.ValidStream(stream) || search?.Length > 200 || offset is < 0 or > 1000000) return Results.BadRequest();
            var db=h.RequestServices.GetRequiredService<NpgsqlDataSource>();
            await using var q=db.CreateCommand("""
                WITH datasets AS (
                  SELECT r.server_id,r.recording_id,count(*) batches,sum(r.byte_count)::bigint bytes,max(r.received_at) received,
                    count(*) FILTER(WHERE r.status='projected') ready,
                    count(*) FILTER(WHERE r.status='received_unvalidated') pending,
                    count(*) FILTER(WHERE r.status IN ('failed','rejected')) problems,
                    coalesce((SELECT max(b.header::json->>'world') FROM gc.telemetry_batches b WHERE $1='telemetry' AND b.server_id=r.server_id AND b.recording_id=r.recording_id),
                      (SELECT max(b.world) FROM gc.mission_batches b WHERE b.stream=$1 AND b.server_id=r.server_id AND b.mission_id=r.recording_id),'') world
                  FROM gc.ingest_receipts r WHERE stream=$1 GROUP BY r.server_id,r.recording_id
                ) SELECT server_id,recording_id,batches,bytes,received,ready,pending,problems,world FROM datasets
                WHERE $2='' OR strpos(lower(world),lower($2))>0 OR strpos(recording_id::text,$2)>0 OR strpos(server_id::text,$2)>0
                ORDER BY received DESC,server_id,recording_id LIMIT 51 OFFSET $3
                """);
            q.CommandTimeout=10;q.Parameters.AddWithValue(stream);q.Parameters.AddWithValue(search??"");q.Parameters.AddWithValue(offset??0);
            var rows=new List<object>();bool more=false;
            await using var r=await q.ExecuteReaderAsync(h.RequestAborted);
            while(await r.ReadAsync(h.RequestAborted)) { if(rows.Count==50){more=true;break;} rows.Add(new {serverId=r.GetGuid(0),id=r.GetGuid(1),batches=r.GetInt64(2),bytes=r.GetInt64(3),received=r.GetDateTime(4),ready=r.GetInt64(5),pending=r.GetInt64(6),problems=r.GetInt64(7),world=r.GetString(8)}); }
            return Results.Ok(new {rows,nextOffset=more?(int?)((offset??0)+50):null});
        });
        routes.MapGet("/{server:guid}/{stream}/{id:guid}/batches",async (HttpContext h,Guid server,string stream,Guid id,int? after)=>
        {
            if(!Intake.ValidStream(stream)||after is < -1)return Results.BadRequest();
            await using var q=h.RequestServices.GetRequiredService<NpgsqlDataSource>().CreateCommand("SELECT revision,byte_count,status,error_code,received_at FROM gc.ingest_receipts WHERE server_id=$1 AND stream=$2 AND recording_id=$3 AND revision>$4 ORDER BY revision LIMIT 101");
            q.Parameters.AddWithValue(server);q.Parameters.AddWithValue(stream);q.Parameters.AddWithValue(id);q.Parameters.AddWithValue(after??-1);
            var rows=new List<object>();int? next=null;int last=-1;
            await using var r=await q.ExecuteReaderAsync(h.RequestAborted);
            while(await r.ReadAsync(h.RequestAborted)){if(rows.Count==100){next=last;break;}last=r.GetInt32(0);rows.Add(new{revision=last,bytes=r.GetInt32(1),status=r.GetString(2),error=r.IsDBNull(3)?null:r.GetString(3),received=r.GetDateTime(4)});}
            return Results.Ok(new{rows,nextAfter=next});
        });
        routes.MapGet("/{server:guid}/{stream}/{id:guid}/batches/{revision:int}",async (HttpContext h,Guid server,string stream,Guid id,int revision)=>
        {
            if(!Intake.ValidStream(stream)||revision<0)return Results.BadRequest();
            var archive=Archive(app);if(archive is null)return Results.StatusCode(503);
            await using var q=h.RequestServices.GetRequiredService<NpgsqlDataSource>().CreateCommand("SELECT archive_name,byte_count,sha256 FROM gc.ingest_receipts WHERE server_id=$1 AND stream=$2 AND recording_id=$3 AND revision=$4");
            q.Parameters.AddWithValue(server);q.Parameters.AddWithValue(stream);q.Parameters.AddWithValue(id);q.Parameters.AddWithValue(revision);
            await using var r=await q.ExecuteReaderAsync(h.RequestAborted);if(!await r.ReadAsync(h.RequestAborted))return Results.NotFound();
            var bytes=await ArchiveFiles.ReadVerified(archive,r.GetString(0),r.GetInt32(1),r.GetString(2),LobbyBatch.MaximumBytes(stream),h.RequestAborted);
            return Results.File(bytes,"application/octet-stream",$"{id}-{stream}-{revision}.batch");
        });
        routes.MapPost("/import/{server:guid}",async (HttpContext h,Guid server,ILoggerFactory logs)=>
        {
            if(server==Guid.Empty)return Results.BadRequest();
            var archive=Archive(app);if(archive is null)return Results.StatusCode(503);
            var limit=h.Features.Get<IHttpMaxRequestBodySizeFeature>();if(limit is {IsReadOnly:false})limit.MaxRequestBodySize=null;
            using var buffer=new MemoryStream();byte[] chunk=new byte[16384];int count;
            while((count=await h.Request.Body.ReadAsync(chunk,h.RequestAborted))>0){if(buffer.Length+count>LobbyBatch.MaximumJsonBytes)return Results.StatusCode(413);await buffer.WriteAsync(chunk.AsMemory(0,count),h.RequestAborted);}
            var bytes=buffer.ToArray();using var batch=LobbyBatch.Read(bytes);
            if(batch.Stream=="history"&&batch.Root.GetProperty("serverId").GetGuid()!=server)return Results.BadRequest(new{error="The history file belongs to a different server."});
            var accepted=await Intake.Receive(h.RequestServices.GetRequiredService<NpgsqlDataSource>(),archive,server,batch.Stream,batch.Id,batch.Revision,bytes,h.RequestAborted);
            logs.CreateLogger("DataExplorer").LogInformation("Admin {User} imported {Server}/{Stream}/{Id}/{Revision}: {Accepted}",h.User.Identity?.Name,server,batch.Stream,batch.Id,batch.Revision,accepted);
            return accepted?Results.Json(new{stream=batch.Stream,id=batch.Id,revision=batch.Revision,status="Accepted; processing may still be pending."},statusCode:202):Results.Conflict(new{error="This revision already exists with different contents."});
        });
        routes.MapDelete("/{server:guid}/{stream}/{id:guid}",async (HttpContext h,Guid server,string stream,Guid id,long expectedBatches,ILoggerFactory logs)=>
        {
            if(!Intake.ValidStream(stream)||server==Guid.Empty||id==Guid.Empty||expectedBatches<1)return Results.BadRequest();
            var removed=await Remove(h.RequestServices.GetRequiredService<NpgsqlDataSource>(),server,stream,id,expectedBatches,h.RequestAborted);
            if(!removed)return Results.Conflict(new{error="Dataset changed. Refresh and review its current batch count."});
            logs.CreateLogger("DataExplorer").LogWarning("Admin {User} removed {Server}/{Stream}/{Id} ({Batches} batches); source archive retained",h.User.Identity?.Name,server,stream,id,expectedBatches);
            return Results.Ok(new{removed=true});
        });
    }
    static string? Archive(WebApplication app)
    {
        var path=app.Configuration["Ingest:ArchivePath"];
        if(string.IsNullOrWhiteSpace(path)||!Path.IsPathFullyQualified(path))return null;
        PrivatePath.EnsureOutsideWebRoot(path,app.Environment.WebRootPath);return path;
    }
    public static async Task<bool> Remove(NpgsqlDataSource db,Guid server,string stream,Guid id,long expected,CancellationToken token)
    {
        if(!Intake.ValidStream(stream))throw new ArgumentException("Invalid stream");
        await using var c=await db.OpenConnectionAsync(token);await using var tx=await c.BeginTransactionAsync(token);
        // Serialize this infrequent admin operation against intake and projection transactions.
        // A short lock timeout prevents an active recording from hanging the admin request.
        await using(var gate=new NpgsqlCommand("SET LOCAL lock_timeout='3s'; LOCK TABLE gc.ingest_receipts IN EXCLUSIVE MODE",c,tx))await gate.ExecuteNonQueryAsync(token);
        NpgsqlCommand Cmd(string sql){var q=new NpgsqlCommand(sql,c,tx);q.Parameters.AddWithValue(server);q.Parameters.AddWithValue(id);q.Parameters.AddWithValue(stream);return q;}
        await using(var q=Cmd("SELECT count(*) FROM gc.ingest_receipts WHERE server_id=$1 AND recording_id=$2 AND stream=$3"))if((long)(await q.ExecuteScalarAsync(token))! != expected)return false;
        var statements=stream switch {
            "telemetry"=>new[]{"DELETE FROM gc.telemetry_batches WHERE server_id=$1 AND recording_id=$2 AND $3='telemetry'"},
            "stats"=>new[]{"DELETE FROM gc.mission_players WHERE server_id=$1 AND mission_id=$2 AND $3='stats'","DELETE FROM gc.mission_batches WHERE server_id=$1 AND mission_id=$2 AND stream=$3"},
            "session"=>new[]{"DELETE FROM gc.session_events WHERE server_id=$1 AND mission_id=$2 AND $3='session'","DELETE FROM gc.mission_batches WHERE server_id=$1 AND mission_id=$2 AND stream=$3"},
            _=>new[]{"DELETE FROM gc.mission_batches WHERE server_id=$1 AND mission_id=$2 AND stream=$3"}};
        foreach(var sql in statements){await using var q=Cmd(sql);await q.ExecuteNonQueryAsync(token);}
        await using(var q=Cmd("DELETE FROM gc.ingest_receipts WHERE server_id=$1 AND recording_id=$2 AND stream=$3"))await q.ExecuteNonQueryAsync(token);
        await tx.CommitAsync(token);return true;
    }
}
