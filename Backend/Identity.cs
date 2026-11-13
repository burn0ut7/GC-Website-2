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
    // Mark HTML routes explicitly. API clients must keep receiving HTTP 401/403.
    internal sealed class ProtectedPage { }

    private static IResult StartDiscord(HttpContext context, IConfiguration configuration, string destination)
    {
        if (!configuration.GetValue<bool>("Discord:Enabled"))
            return Notice("Sign-in unavailable", "Discord sign-in is not configured.", 503);
        if (!context.Request.IsHttps)
            return Notice("Secure connection required", "Open the HTTPS website to sign in.", 400);
        return Results.Challenge(new AuthenticationProperties { RedirectUri = destination, IsPersistent = true }, ["discord"]);
    }

    private static IResult Notice(string title, string message, int status, bool retry = false) =>
        Results.Content($"""
        <!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <title>{title} · Global Conflicts</title><link rel="stylesheet" href="/account.css">
        <main><h1>{title}</h1><p>{message}</p>{(retry ? "<p><a href='/auth/discord'>Try Discord sign-in again</a></p>" : "")}<a href="/">Back to Global Conflicts</a></main></html>
        """, "text/html; charset=utf-8", statusCode: status);

    public static void Configure(WebApplicationBuilder builder)
    {
        SteamLink.Configure(builder.Services);
        PatreonLink.Configure(builder.Services);
        builder.Services.AddHostedService<DonationRefresh>();
        var authentication = builder.Services.AddAuthentication("gc-session").AddCookie("gc-session", options =>
        {
            options.Cookie.Name = "__Host-GC.Session";
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.ExpireTimeSpan = TimeSpan.FromDays(30);
            options.SlidingExpiration = false;
            options.Events.OnRedirectToLogin = context =>
            {
                if (context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<ProtectedPage>() is not null)
                    return StartDiscord(context.HttpContext, builder.Configuration,
                        context.Request.PathBase.Add(context.Request.Path).ToUriComponent() + context.Request.QueryString.ToUriComponent()).ExecuteAsync(context.HttpContext);
                context.Response.StatusCode = 401;
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = context =>
            {
                if (context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<ProtectedPage>() is not null)
                    context.Response.Redirect(context.Request.PathBase + "/insufficient-permissions");
                else context.Response.StatusCode = 403;
                return Task.CompletedTask;
            };
        });
        builder.Services.AddAuthorization(options => options.AddPolicy("administrator", policy =>
            policy.RequireAuthenticatedUser().AddRequirements(new AdministratorRequirement())));
        builder.Services.AddScoped<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, AdministratorHandler>();
        builder.Services.AddSingleton<DiscordDeveloperAccess>();
        builder.Services.AddSingleton<AccountRoles>();
        builder.Services.AddHttpClient("discord-roles", client =>
            {
                client.Timeout = TimeSpan.FromSeconds(3);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("DiscordBot (https://globalconflicts.net, 1.0)");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
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
                if (context.Request.Query.TryGetValue("iss", out var issuer) && issuer != "https://discord.com")
                    throw new InvalidOperationException("Unexpected OAuth issuer.");
                using var request = new HttpRequestMessage(HttpMethod.Get, context.Options.UserInformationEndpoint);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);
                using var response = await context.Backchannel.SendAsync(request, context.HttpContext.RequestAborted);
                response.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(context.HttpContext.RequestAborted));
                var subject = json.RootElement.GetProperty("id").GetString()!;
                var name = json.RootElement.GetProperty("username").GetString()!;
                var database = context.HttpContext.RequestServices.GetRequiredService<NpgsqlDataSource>();
                var avatar = json.RootElement.TryGetProperty("avatar", out var avatarValue) ? avatarValue.GetString() : null;
                var id = await Accounts.SignInDiscord(database, subject, name, context.HttpContext.RequestAborted, avatar);
                context.Identity!.AddClaim(new Claim(ClaimTypes.NameIdentifier, id.ToString()));
                context.Identity.AddClaim(new Claim(ClaimTypes.Name, name));
            };
            options.Events.OnRemoteFailure = context =>
            {
                context.HandleResponse();
                var cause = context.Failure?.GetBaseException();
                context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("DiscordSignIn")
                    .LogWarning("Discord sign-in failed ({Cause}, trace {Trace})", cause?.GetType().Name, context.HttpContext.TraceIdentifier);
                var databaseUnavailable = false;
                for (var error = context.Failure; error is not null; error = error.InnerException)
                    if (error is NpgsqlException) { databaseUnavailable = true; break; }
                return Notice("Discord sign-in couldn’t finish", databaseUnavailable
                    ? "The account database is unavailable. Please try again shortly."
                    : "Please start sign-in again in this browser.", databaseUnavailable ? 503 : 400, true).ExecuteAsync(context.HttpContext);
            };
        });
    }

    public static void Map(WebApplication app)
    {
        AccountSettingsRoutes.Map(app);
        AccountRoles.Map(app);
        SteamLink.Map(app);
        PatreonLink.Map(app);
        SiteNavigation.Map(app);
        DonationRoutes.Map(app);
        app.MapGet("/account.html", () => Results.File(Path.Combine(app.Environment.WebRootPath, "account.html"), "text/html"))
            .WithMetadata(new ProtectedPage()).RequireAuthorization();
        app.MapGet("/auth/discord", (HttpContext context) => StartDiscord(context, app.Configuration, context.Request.PathBase + "/account.html"));
        app.MapGet("/insufficient-permissions", () => Notice("Insufficient permissions", "Your Discord account does not have access to this page.", 200));
        app.MapGet("/api/auth/status", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { discordEnabled = app.Configuration.GetValue<bool>("Discord:Enabled"), https = context.Request.IsHttps });
        });
        app.MapGet("/api/me", async (HttpContext context, Microsoft.AspNetCore.Authorization.IAuthorizationService authorization) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var database = context.RequestServices.GetService<NpgsqlDataSource>();
            if (database is null) return Results.StatusCode(503);
            if (!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)) return Results.Unauthorized();
            var discord = await Accounts.DiscordIdentity(database, id, context.RequestAborted);
            if (discord is null) return Results.Unauthorized();
            return Results.Ok(new { id, name = discord.Value.Name, discordId = discord.Value.Subject,
                isAdmin = (await authorization.AuthorizeAsync(context.User, null, "administrator")).Succeeded });
        }).RequireAuthorization();
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
