using System.Security.Cryptography;
using System.Text;
using GC.Infrastructure;
using Npgsql;

namespace GC.Web;

public static class Integrations
{
    public static void Map(WebApplication app)
    {
        var key = app.Configuration["Ingest:ApiKey"];
        var archive = app.Configuration["Ingest:ArchivePath"];
        var enabled = app.Configuration.GetValue<bool>("Ingest:Enabled");
        var server = Guid.Empty;
        if (enabled)
        {
            if (key?.Length < 32 || string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(archive) ||
                !Path.IsPathFullyQualified(archive) || !Guid.TryParse(app.Configuration["Ingest:ServerId"], out server) || server == Guid.Empty)
                throw new InvalidOperationException("Intake requires a 32+ character API key, server UUID and absolute private archive path.");
            PrivatePath.EnsureOutsideWebRoot(archive, app.Environment.WebRootPath);
        }
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(key ?? ""));
        IResult? AuthorizeServer(HttpContext context)
        {
            if (!enabled) return Results.Json(new { error = "intake_not_configured" }, statusCode: 503);
            var header = context.Request.Headers.Authorization.ToString();
            if (!header.StartsWith("Bearer ", StringComparison.Ordinal) ||
                !CryptographicOperations.FixedTimeEquals(expected, SHA256.HashData(Encoding.UTF8.GetBytes(header[7..]))))
                return Results.Unauthorized();
            if (!context.Request.IsHttps && !app.Environment.IsDevelopment())
                return Results.BadRequest(new { error = "https_required" });
            return null;
        }
        app.MapPost("/api/v1/ingest/{stream}/{recording:guid}/{revision:int}", async (HttpContext context, string stream, Guid recording, int revision) =>
        {
            if (AuthorizeServer(context) is { } denial) return denial;
            if (!Intake.ValidStream(stream) || revision < 0 || recording == Guid.Empty) return Results.BadRequest();
            var database = context.RequestServices.GetService<NpgsqlDataSource>();
            if (database is null) return Results.StatusCode(503);
            if (context.Request.ContentLength > Intake.MaximumBytes) return Results.StatusCode(413);
            using var memory = new MemoryStream();
            var buffer = new byte[16384];
            int length;
            while ((length = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
            {
                if (memory.Length + length > Intake.MaximumBytes) return Results.StatusCode(413);
                await memory.WriteAsync(buffer.AsMemory(0, length), context.RequestAborted);
            }
            if (memory.Length == 0) return Results.BadRequest();
            var accepted = await Intake.Receive(database, archive!, server, stream, recording, revision, memory.ToArray(), context.RequestAborted);
            return accepted ? Results.Json(new { status = "accepted" }, statusCode: 202) : Results.Conflict(new { error = "revision_conflict" });
        }).RequireRateLimiting("intake");
        // Proof APIs use the same server-scoped machine credential, not blanket member access.
        app.MapGet("/api/v1/recordings/{recording:guid}", async (HttpContext context, Guid recording) =>
        {
            if (AuthorizeServer(context) is { } denial) return denial;
            var database = context.RequestServices.GetService<NpgsqlDataSource>();
            if (database is null) return Results.StatusCode(503);
            context.Response.Headers.CacheControl = "no-store";
            var summary = await Recordings.Summary(database, server, recording, context.RequestAborted);
            return summary is null ? Results.NotFound() : Results.Ok(summary);
        }).RequireRateLimiting("intake");
        app.MapGet("/api/v1/recordings/{recording:guid}/records", async (HttpContext context, Guid recording,
            double fromMs, double untilMs, int? afterSequence, int? limit) =>
        {
            if (AuthorizeServer(context) is { } denial) return denial;
            var database = context.RequestServices.GetService<NpgsqlDataSource>();
            if (database is null) return Results.StatusCode(503);
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                return Results.Ok(await Recordings.Range(database, archive!, server, recording, fromMs, untilMs,
                    afterSequence ?? 0, limit ?? 250, context.RequestAborted));
            }
            catch (ArgumentException) { return Results.BadRequest(new { error = "invalid_range" }); }
            catch (InvalidDataException) { return Results.Json(new { error = "archive_integrity" }, statusCode: 503); }
            catch (IOException) { return Results.Json(new { error = "archive_unavailable" }, statusCode: 503); }
            catch (UnauthorizedAccessException) { return Results.Json(new { error = "archive_unavailable" }, statusCode: 503); }
        }).RequireRateLimiting("intake");
    }
}
