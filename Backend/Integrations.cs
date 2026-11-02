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
        app.MapPost("/api/v1/ingest/{stream}/{recording:guid}/{revision:int}", async (HttpContext context, string stream, Guid recording, int revision) =>
        {
            if (!enabled) return Results.Json(new { error = "intake_not_configured" }, statusCode: 503);
            var header = context.Request.Headers.Authorization.ToString();
            if (!header.StartsWith("Bearer ", StringComparison.Ordinal) ||
                !CryptographicOperations.FixedTimeEquals(expected, SHA256.HashData(Encoding.UTF8.GetBytes(header[7..]))))
                return Results.Unauthorized();
            if (!context.Request.IsHttps && !app.Environment.IsDevelopment())
                return Results.BadRequest(new { error = "https_required" });
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
            return accepted ? Results.Json(new { status = "received_unvalidated" }, statusCode: 202) : Results.Conflict(new { error = "revision_conflict" });
        }).RequireRateLimiting("intake");
    }
}
