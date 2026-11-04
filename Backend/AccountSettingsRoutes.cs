using System.Security.Claims;
using System.Text.Json;
using GC.Infrastructure;
using Microsoft.AspNetCore.Antiforgery;
using Npgsql;

namespace GC.Web;

public static class AccountSettingsRoutes
{
    public static void Map(WebApplication app)
    {
        var routes = app.MapGroup("/api/account/settings").RequireAuthorization();
        routes.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            http.Response.Headers.CacheControl = "no-store";
            var database = http.RequestServices.GetService<NpgsqlDataSource>();
            if (database is null) return Results.StatusCode(503);
            if (!Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var user)
                || await Accounts.DiscordIdentity(database,user,http.RequestAborted) is null) return Results.Unauthorized();
            return await next(context);
        });
        routes.MapGet("", async Task<IResult> (HttpContext context) => Results.Ok(
            await AccountPreferences.Read(context.RequestServices.GetRequiredService<NpgsqlDataSource>(), User(context), context.RequestAborted)));
        routes.MapPut("", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { return Results.BadRequest(new { error = "csrf_required" }); }
            if (!context.Request.HasJsonContentType()) return Results.StatusCode(415);
            // Preferences are small; bound chunked bodies too.
            using var body = new MemoryStream();
            var buffer = new byte[4096];
            int count;
            while ((count = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
            {
                if (body.Length + count > 8192) return Results.StatusCode(413);
                body.Write(buffer, 0, count);
            }
            try
            {
                var value = JsonSerializer.Deserialize<AccountPreferences>(body.ToArray(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (value is null) return Results.BadRequest(new { error = "invalid_settings" });
                return Results.Ok(await AccountPreferences.Save(context.RequestServices.GetRequiredService<NpgsqlDataSource>(), User(context), value, context.RequestAborted));
            }
            catch (Exception error) when (error is JsonException or ArgumentException)
            { return Results.BadRequest(new { error = "invalid_settings" }); }
        });
    }

    private static Guid User(HttpContext context) => Guid.Parse(context.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
