using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using GC.Infrastructure;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;

namespace GC.Web;

public static class Identity
{
    public static void Configure(WebApplicationBuilder builder)
    {
        var authentication = builder.Services.AddAuthentication("gc-session").AddCookie("gc-session", options =>
        {
            options.Cookie.Name = "__Host-GC.Session";
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = false;
            options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
            options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
        });
        builder.Services.AddAuthorization();
        builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
        if (!builder.Configuration.GetValue<bool>("Discord:Enabled")) return;
        var clientId = Required(builder.Configuration, "Discord:ClientId");
        var secret = Required(builder.Configuration, "Discord:ClientSecret");
        _ = Required(builder.Configuration, "ConnectionStrings:Gc");
        var keys = Path.GetFullPath(Required(builder.Configuration, "Auth:KeysPath"));
        PrivatePath.EnsureOutsideWebRoot(keys, builder.Environment.WebRootPath);
        var protection = builder.Services.AddDataProtection().SetApplicationName("GC.Web")
            .PersistKeysToFileSystem(new DirectoryInfo(keys));
        if (OperatingSystem.IsWindows()) protection.ProtectKeysWithDpapi();
        authentication.AddOAuth("discord", options =>
        {
            options.SignInScheme = "gc-session";
            options.ClientId = clientId; options.ClientSecret = secret;
            options.CallbackPath = "/auth/discord/callback";
            options.AuthorizationEndpoint = "https://discord.com/oauth2/authorize";
            options.TokenEndpoint = "https://discord.com/api/oauth2/token";
            options.UserInformationEndpoint = "https://discord.com/api/users/@me";
            options.Scope.Add("identify");
            options.UsePkce = true; options.SaveTokens = false;
            options.Events.OnCreatingTicket = async context =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, context.Options.UserInformationEndpoint);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);
                using var response = await context.Backchannel.SendAsync(request, context.HttpContext.RequestAborted);
                response.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(context.HttpContext.RequestAborted));
                var subject = json.RootElement.GetProperty("id").GetString()!;
                var name = json.RootElement.GetProperty("username").GetString()!;
                var database = context.HttpContext.RequestServices.GetRequiredService<NpgsqlDataSource>();
                var id = await Accounts.SignInDiscord(database, subject, name, context.HttpContext.RequestAborted);
                context.Identity!.AddClaim(new Claim(ClaimTypes.NameIdentifier, id.ToString()));
                context.Identity.AddClaim(new Claim(ClaimTypes.Name, name));
            };
            options.Events.OnRemoteFailure = context =>
            {
                context.HandleResponse();
                context.Response.StatusCode = 400;
                return context.Response.WriteAsJsonAsync(new { error = "discord_sign_in_failed" });
            };
        });
    }

    public static void Map(WebApplication app)
    {
        app.MapGet("/auth/discord", (HttpContext context) =>
        {
            if (!app.Configuration.GetValue<bool>("Discord:Enabled"))
                return Results.Json(new { error = "discord_not_configured" }, statusCode: 503);
            if (!context.Request.IsHttps) return Results.BadRequest(new { error = "https_required" });
            return Results.Challenge(new AuthenticationProperties { RedirectUri = "/#home" }, ["discord"]);
        });
        app.MapGet("/api/me", (ClaimsPrincipal user) => Results.Ok(new
        {
            id = user.FindFirstValue(ClaimTypes.NameIdentifier), name = user.Identity?.Name
        })).RequireAuthorization();
        app.MapGet("/api/auth/csrf", (HttpContext context, IAntiforgery antiforgery) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { token = antiforgery.GetAndStoreTokens(context).RequestToken });
        }).RequireAuthorization();
        app.MapPost("/auth/logout", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            try { await antiforgery.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { return Results.BadRequest(); }
            await context.SignOutAsync("gc-session");
            return Results.NoContent();
        }).RequireAuthorization();
    }

    private static string Required(IConfiguration configuration, string name) =>
        !string.IsNullOrWhiteSpace(configuration[name]) ? configuration[name]! : throw new InvalidOperationException(name + " is required.");
}

public static class PrivatePath
{
    public static void EnsureOutsideWebRoot(string path, string webRoot)
    {
        var root = Path.GetFullPath(webRoot).TrimEnd(Path.DirectorySeparatorChar);
        var candidate = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if (candidate.Equals(root, StringComparison.OrdinalIgnoreCase) || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Private storage must be outside the public web root.");
    }
}
