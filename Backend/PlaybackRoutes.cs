using Npgsql;
namespace GC.Web;
public static class PlaybackRoutes
{
    public static void Map(WebApplication app,RouteGroupBuilder admin)
    {
        app.MapGet("/playback",()=>Results.File(Path.Combine(AppContext.BaseDirectory,"PrivatePages","playback.html"),"text/html"))
            .WithMetadata(new Identity.ProtectedPage()).RequireAuthorization(SiteNavigation.AdminPolicy);
        admin.MapGet("/recordings",async Task<IResult> (HttpContext context,int? offset,long? startedFrom,long? startedUntil)=>
        {
            var skip=offset??0;if(skip<0||skip>1000000)return Results.BadRequest();
            if ((startedFrom.HasValue != startedUntil.HasValue) || (startedFrom.HasValue && (startedFrom < 0 || startedUntil <= startedFrom))) return Results.BadRequest();
            var database=context.RequestServices.GetService<NpgsqlDataSource>();if(database is null)return Results.StatusCode(503);
            await using var query=database.CreateCommand("""
                SELECT server_id,recording_id,mission_id,min(from_ms),max(until_ms),sum(record_count)::bigint,
                    bool_or(closed),max(header::json->>'world'),max((header::json->>'startedAt')::bigint),
                    (SELECT h.header::json->>'missionName' FROM gc.mission_batches h
                     WHERE h.server_id=t.server_id AND h.mission_id=t.mission_id AND h.stream='history'
                     ORDER BY h.revision DESC LIMIT 1)
                FROM gc.telemetry_batches t GROUP BY server_id,recording_id,mission_id
                HAVING ($2::bigint IS NULL OR max((header::json->>'startedAt')::bigint)>=$2)
                   AND ($3::bigint IS NULL OR max((header::json->>'startedAt')::bigint)<$3)
                ORDER BY max((header::json->>'startedAt')::bigint) DESC,server_id,recording_id LIMIT 51 OFFSET $1
                """);
            query.CommandTimeout=10;query.Parameters.AddWithValue(skip);
            query.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Bigint,(object?)startedFrom??DBNull.Value);
            query.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Bigint,(object?)startedUntil??DBNull.Value);var rows=new List<object>();bool more=false;
            await using var reader=await query.ExecuteReaderAsync(context.RequestAborted);
            while(await reader.ReadAsync(context.RequestAborted)){if(rows.Count==50){more=true;break;}rows.Add(new{serverId=reader.GetGuid(0),recordingId=reader.GetGuid(1),missionId=reader.GetGuid(2),fromMs=reader.GetDouble(3),untilMs=reader.GetDouble(4),recordCount=reader.GetInt64(5),closed=reader.GetBoolean(6),world=reader.GetString(7),startedAt=reader.GetInt64(8),missionName=reader.IsDBNull(9)?null:reader.GetString(9)});}
            return Results.Ok(new{recordings=rows,nextOffset=more?(int?)(skip+50):null});
        });
    }
}
