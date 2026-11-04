using GC.Infrastructure;
using Npgsql;

namespace GC.Web;

public static class AdminData
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/admin", () => Results.File(Path.Combine(AppContext.BaseDirectory, "PrivatePages", "admin.html"), "text/html"))
            .WithMetadata(new Identity.ProtectedPage()).RequireAuthorization(SiteNavigation.AdminPolicy);
        var routes = app.MapGroup("/api/admin").RequireAuthorization(SiteNavigation.AdminPolicy).RequireRateLimiting("intake");
        GameRecordRoutes.Map(app,routes);
        AdminDatabase.Map(routes);
        DataExplorer.Map(app,routes);
        PlaybackRoutes.Map(app,routes);
        MissionRoutes.Map(routes.MapGroup("/missions/{server:guid}"),context=>Guid.Parse((string)context.Request.RouteValues["server"]!));
        routes.MapGet("/data", async (HttpContext context) =>
        {
            var database = context.RequestServices.GetService<NpgsqlDataSource>();
            if (database is null) return Results.StatusCode(503);
            var streams = new List<object>();
            await using (var query = database.CreateCommand("""
                SELECT stream,status,count(*),max(received_at) FROM gc.ingest_receipts GROUP BY stream,status ORDER BY stream,status
                """))
            {
                await using var reader = await query.ExecuteReaderAsync(context.RequestAborted);
                while (await reader.ReadAsync(context.RequestAborted))
                    streams.Add(new { stream = reader.GetString(0), status = reader.GetString(1), batches = reader.GetInt64(2), lastReceived = reader.GetDateTime(3) });
            }
            var recent = new List<object>();
            await using (var query = database.CreateCommand("""
                SELECT server_id,stream,recording_id,revision,byte_count,received_at,status,error_code
                FROM gc.ingest_receipts ORDER BY received_at DESC,server_id,stream,recording_id,revision DESC LIMIT 50
                """))
            {
                await using var reader = await query.ExecuteReaderAsync(context.RequestAborted);
                while (await reader.ReadAsync(context.RequestAborted))
                    recent.Add(new { serverId = reader.GetGuid(0), stream = reader.GetString(1), recordingId = reader.GetGuid(2),
                        revision = reader.GetInt32(3), bytes = reader.GetInt32(4), receivedAt = reader.GetDateTime(5),
                        status = reader.GetString(6), error = reader.IsDBNull(7) ? null : reader.GetString(7) });
            }
            return Results.Ok(new { streams, recent, recentLimit = 50,
                scope = "Website intake database only. Existing Lobby2 importer databases are separate. Processing status applies independently to each stream; missing data is not confirmed zero." });
        });
        routes.MapGet("/recordings/{server:guid}/{recording:guid}", async (HttpContext context, Guid server, Guid recording) =>
        {
            var database = context.RequestServices.GetService<NpgsqlDataSource>();
            if (database is null) return Results.StatusCode(503);
            var summary = await Recordings.Summary(database, server, recording, context.RequestAborted);
            return summary is null ? Results.NotFound() : Results.Ok(summary);
        });
        routes.MapGet("/recordings/{server:guid}/{recording:guid}/records", async (HttpContext context, Guid server, Guid recording,
            double fromMs, double untilMs, int? afterSequence, int? limit, string? type) =>
        {
            var database = context.RequestServices.GetService<NpgsqlDataSource>();
            var archive = app.Configuration["Ingest:ArchivePath"];
            if (database is null || string.IsNullOrWhiteSpace(archive) || !Path.IsPathFullyQualified(archive))
                return Results.Json(new { error = "archive_not_configured" }, statusCode: 503);
            PrivatePath.EnsureOutsideWebRoot(archive, app.Environment.WebRootPath);
            try
            {
                return Results.Ok(await Recordings.Range(database, archive, server, recording, fromMs, untilMs,
                    afterSequence ?? 0, limit ?? 100, context.RequestAborted, type));
            }
            catch (ArgumentException) { return Results.BadRequest(new { error = "invalid_range" }); }
            catch (InvalidDataException) { return Results.Json(new { error = "archive_integrity" }, statusCode: 503); }
            catch (IOException) { return Results.Json(new { error = "archive_unavailable" }, statusCode: 503); }
            catch (UnauthorizedAccessException) { return Results.Json(new { error = "archive_unavailable" }, statusCode: 503); }
        });
    }
}
