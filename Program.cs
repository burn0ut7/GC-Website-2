using Microsoft.AspNetCore.HttpLogging;
using GC.Infrastructure;
using Npgsql;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "dist")
});

var connectionString = builder.Configuration.GetConnectionString("Gc");
if (args.Contains("--migrate", StringComparer.Ordinal))
{
    if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("ConnectionStrings__Gc is required for migrations.");
    await using var migrationSource = Database.Open(connectionString);
    await Database.Migrate(migrationSource);
    Console.WriteLine("Database migrations applied.");
    return;
}
if (!string.IsNullOrWhiteSpace(connectionString))
    builder.Services.AddSingleton(_ => Database.Open(connectionString));

builder.Services.AddHttpLogging(options =>
{
    options.LoggingFields = HttpLoggingFields.RequestMethod
        | HttpLoggingFields.RequestPath
        | HttpLoggingFields.ResponseStatusCode
        | HttpLoggingFields.Duration;
    options.CombineLogs = true;
});

var app = builder.Build();
app.UseHttpLogging();
app.UseDefaultFiles();
app.UseStaticFiles();

// Liveness only: database and external services are not part of this slice.
app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/api/ready", async (HttpContext context) =>
{
    var database = context.RequestServices.GetService<NpgsqlDataSource>();
    if (database is null) return Results.Json(new { status = "not_configured" }, statusCode: 503);
    try
    {
        return await Database.IsReady(database, context.RequestAborted)
            ? Results.Ok(new { status = "ready" })
            : Results.Json(new { status = "schema_mismatch" }, statusCode: 503);
    }
    catch (Exception error) when (error is NpgsqlException or TimeoutException)
    {
        return Results.Json(new { status = "unavailable" }, statusCode: 503);
    }
});

app.Run();
