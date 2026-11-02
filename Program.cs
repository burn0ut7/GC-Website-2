using Microsoft.AspNetCore.HttpLogging;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "dist")
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
app.UseHttpLogging();
app.UseDefaultFiles();
app.UseStaticFiles();

// Liveness only: database and external services are not part of this slice.
app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

app.Run();
