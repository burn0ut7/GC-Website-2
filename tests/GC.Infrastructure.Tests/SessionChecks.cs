using System.Net;
using System.Security.Claims;
using GC.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

internal static class SessionChecks
{
    public static async Task Run()
    {
        var keys = Path.Combine(Path.GetTempPath(), "gc-session-test-" + Guid.NewGuid().ToString("N"));
        var clock = new DiscordRoleChecks.Clock();
        var issued = clock.GetUtcNow();
        string? cookie = null;
        try
        {
            for (var run = 0; run < 2; run++)
            {
                var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
                builder.Logging.ClearProviders();
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                builder.Environment.WebRootPath = Path.Combine(AppContext.BaseDirectory, "dist");
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Discord:Enabled"] = "true", ["Discord:ClientId"] = "test", ["Discord:ClientSecret"] = "test",
                    ["ConnectionStrings:Gc"] = "test", ["Auth:KeysPath"] = keys
                });
                Identity.Configure(builder);
                builder.Services.PostConfigure<CookieAuthenticationOptions>("gc-session", options => options.TimeProvider = clock);
                await using var app = builder.Build();
                app.Use(async (context, next) => { context.Request.Scheme = "https"; await next(context); });
                app.UseAuthentication(); app.UseAuthorization();
                Identity.Map(app);
                // Test-only endpoint uses the actual Discord challenge properties and production cookie handler.
                // No database or Discord credentials are needed for session lifetime/restart checks.
                AuthenticationProperties? properties = null;
                app.MapGet("/test/sign-in", async context => await context.SignInAsync("gc-session",
                    new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "test") }, "test")), properties!));
                app.MapGet("/test/private", () => "ok").RequireAuthorization();
                await app.StartAsync();
                using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
                { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
                if (run == 0)
                {
                    using var challenge = await client.GetAsync("/auth/discord");
                    var state = QueryHelpers.ParseQuery(challenge.Headers.Location!.Query)["state"].ToString();
                    properties = app.Services.GetRequiredService<IOptionsMonitor<OAuthOptions>>().Get("discord").StateDataFormat.Unprotect(state);
                    if (properties?.IsPersistent != true) throw new Exception("Discord login is not persistent by default");
                    using var signIn = await client.GetAsync("/test/sign-in");
                    var header = signIn.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("__Host-GC.Session="));
                    var parsed = Microsoft.Net.Http.Headers.SetCookieHeaderValue.Parse(header);
                    if (!parsed.Secure || !parsed.HttpOnly || parsed.SameSite != Microsoft.Net.Http.Headers.SameSiteMode.Lax
                        || parsed.Expires is not {} expires || Math.Abs((expires - issued).TotalDays - 30) > 0.001)
                        throw new Exception("Persistent cookie lifetime or protections incorrect");
                    cookie = header.Split(';')[0];
                }
                client.DefaultRequestHeaders.Add("Cookie", cookie);
                clock.Now = issued.AddDays(29);
                using var valid = await client.GetAsync("/test/private");
                if (valid.StatusCode != HttpStatusCode.OK || valid.Headers.Contains("Set-Cookie"))
                    throw new Exception("Session lost before expiry/restart or unexpectedly renewed");
                if (run == 1)
                {
                    using var csrfResponse = await client.GetAsync("/api/auth/csrf");
                    using var csrf = System.Text.Json.JsonDocument.Parse(await csrfResponse.Content.ReadAsStringAsync());
                    client.DefaultRequestHeaders.Remove("Cookie");
                    client.DefaultRequestHeaders.Add("Cookie", cookie + "; " + string.Join("; ", csrfResponse.Headers.GetValues("Set-Cookie").Select(x => x.Split(';')[0])));
                    using var logout = new HttpRequestMessage(HttpMethod.Post, "/auth/logout");
                    logout.Headers.Add("X-CSRF-TOKEN", csrf.RootElement.GetProperty("token").GetString());
                    using var response = await client.SendAsync(logout);
                    if (response.StatusCode != HttpStatusCode.NoContent || !response.Headers.GetValues("Set-Cookie").Any(x => x.StartsWith("__Host-GC.Session=;")))
                        throw new Exception("Logout did not clear persistent cookie");
                    clock.Now = issued.AddDays(30).AddSeconds(1);
                    using var expired = await client.GetAsync("/test/private");
                    if (expired.StatusCode != HttpStatusCode.Unauthorized) throw new Exception("Expired session accepted");
                }
                await app.StopAsync();
            }
            Console.WriteLine("PASS: Default persistent 30-day cookie, secure flags, new host/key reload, fixed expiry and CSRF logout");
        }
        finally { if (Directory.Exists(keys)) Directory.Delete(keys, true); }
    }
}
