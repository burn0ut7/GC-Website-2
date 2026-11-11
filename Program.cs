using Microsoft.AspNetCore.HttpLogging;
using GC.Infrastructure;
using Npgsql;
using GC.Web;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "dist")
});

// Optional private local configuration. Environment/command-line settings still win.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables().AddCommandLine(args);

var connectionString = builder.Configuration.GetConnectionString("Gc");
if (args.Contains("--migrate", StringComparer.Ordinal))
{
    if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("ConnectionStrings__Gc is required for migrations.");
    await using var migrationSource = Database.Open(connectionString);
    await Database.Migrate(migrationSource);
    Console.WriteLine("Database migrations applied.");
    return;
}
if (Array.IndexOf(args,"--import-local-mission") is var importAt && importAt >= 0)
{
    if(importAt+1>=args.Length || string.IsNullOrWhiteSpace(connectionString))throw new ArgumentException("A mission manifest and database connection are required.");
    await using var db=Database.Open(connectionString);
    var manifest=await File.ReadAllTextAsync(args[importAt+1]);
    if(Array.IndexOf(args,"--mission-header") is var headerAt && headerAt>=0)
    {
        if(headerAt+1>=args.Length)throw new ArgumentException("A mission header path is required.");
        if(args.Contains("--mission-content"))throw new ArgumentException("Generated content now belongs in the mission header. Refresh it in Workbench and omit --mission-content.");
        manifest=LocalMissionImport.WithHeader(manifest,await File.ReadAllTextAsync(args[headerAt+1]));
    }
    var id=await LocalMissionImport.Import(db,manifest,args.Contains("--replace-mission-list"));
    Console.WriteLine("Imported mission: "+id);return;
}
if (!string.IsNullOrWhiteSpace(connectionString))
    builder.Services.AddSingleton(_ => Database.Open(connectionString));
builder.Services.AddProblemDetails();
Identity.Configure(builder);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddConcurrencyLimiter("intake", limiter => { limiter.PermitLimit = 2; limiter.QueueLimit = 0; });
});

builder.Services.AddHttpLogging(options =>
{
    options.LoggingFields = HttpLoggingFields.RequestMethod
        | HttpLoggingFields.RequestPath
        | HttpLoggingFields.ResponseStatusCode
        | HttpLoggingFields.Duration;
    options.CombineLogs = true;
});

var app = builder.Build();
app.UseExceptionHandler();
app.UseHttpLogging();
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/auth") || context.Request.Path.StartsWithSegments("/api/auth") ||
        context.Request.Path.StartsWithSegments("/api/me") || context.Request.Path.StartsWithSegments("/api/admin") ||
        context.Request.Path.StartsWithSegments("/admin") || context.Request.Path.StartsWithSegments("/playback") ||
        context.Request.Path == "/account.html" || context.Request.Path == "/insufficient-permissions")
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["X-Frame-Options"] = "DENY";
    }
    await next(context);
});
app.UseAuthentication();
app.UseAuthorization();
app.UseDefaultFiles();
app.UseStaticFiles();
Identity.Map(app);
AdminData.Map(app);
MapPackages.Map(app);
Integrations.Map(app);
CatalogRoutes.Map(app);

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
