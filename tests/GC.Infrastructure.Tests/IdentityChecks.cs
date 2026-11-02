using System.Net;
using System.Text;
using System.Text.Json;
using GC.Web;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

internal static class IdentityChecks
{
    public static async Task Run(NpgsqlDataSource database)
    {
        var keys = Path.Combine(Path.GetTempPath(), "gc-auth-test-" + Guid.NewGuid().ToString("N"));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Environment.WebRootPath = Path.Combine(AppContext.BaseDirectory, "dist");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Discord:Enabled"] = "true", ["Discord:ClientId"] = "test-client", ["Discord:ClientSecret"] = "test-secret",
            ["ConnectionStrings:Gc"] = "configured-in-test", ["Auth:KeysPath"] = keys,
            ["Discord:GuildId"] = "444444444444444444", ["Discord:DeveloperRoleId"] = "555555555555555555",
            ["Discord:BotToken"] = "test-bot-token",
            // Legacy user grants must no longer confer any permission.
            ["Auth:AdminDiscordIds:0"] = "333333333333333333"
        });
        builder.Services.AddSingleton(database);
        Identity.Configure(builder);
        var provider = new DiscordStub();
        builder.Services.AddHttpClient("discord-roles").ConfigurePrimaryHttpMessageHandler(() => provider);
        builder.Services.PostConfigure<OAuthOptions>("discord", options => options.Backchannel = new HttpClient(provider));
        builder.Services.AddRateLimiter(options => options.AddConcurrencyLimiter("intake", limit => limit.PermitLimit = 2));
        await using var app = builder.Build();
        // HTTPS semantics for the real OAuth/cookie handlers; transport stays isolated loopback.
        // This middleware exists only in this test host, never in the shipped website.
        app.Use(async (context, next) => { context.Request.Scheme = "https"; await next(context); });
        app.UseRateLimiter(); app.UseAuthentication(); app.UseAuthorization();
        Identity.Map(app); AdminData.Map(app);
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = new Uri(address) };
            await Check(client, "/api/admin/data", 401);
            await Check(client, "/auth/discord/callback?code=admin&state=forged", 400);
            var member = await Login(client, "member");
            client.DefaultRequestHeaders.Add("Cookie", member);
            await Check(client, "/api/admin/data", 403);
            await Check(client, "/admin", 403);
            await Check(client, $"/api/admin/missions/{Guid.NewGuid()}", 403);
            await Check(client, $"/api/admin/recordings/{Guid.NewGuid()}/{Guid.NewGuid()}/records?fromMs=0&untilMs=1000", 403);
            client.DefaultRequestHeaders.Remove("Cookie");
            var administrator = await Login(client, "admin");
            client.DefaultRequestHeaders.Add("Cookie", administrator);
            using var me = JsonDocument.Parse(await client.GetStringAsync("/api/me"));
            var account = me.RootElement.GetProperty("id").GetGuid();
            if (!me.RootElement.GetProperty("isAdmin").GetBoolean() || me.RootElement.GetProperty("discordId").GetString() != "222222222222222222")
                throw new Exception("Verified Discord account not reflected in profile");
            await Check(client, "/api/admin/data", 200);
            await Check(client, "/admin", 200);
            await Check(client, $"/api/admin/recordings/{Guid.NewGuid()}/{Guid.NewGuid()}", 404);
            provider.Developer = false;
            await Check(client, "/api/admin/data", 403);
            provider.Developer = true;
            await Check(client, "/api/admin/data", 200);
            foreach(var failure in new[]{HttpStatusCode.NotFound,HttpStatusCode.Forbidden,HttpStatusCode.ServiceUnavailable})
            {
                provider.RoleStatus=failure;
                await Check(client, "/api/admin/data", 403);
                await Check(client, "/api/me", 200); // Provider trouble must not break ordinary login.
            }
            provider.RoleStatus=HttpStatusCode.OK;
            provider.MalformedRoles=true;
            await Check(client, "/api/admin/data", 403);
            provider.MalformedRoles=false;
            provider.WrongIdentity=true;
            await Check(client, "/api/admin/data", 403);
            provider.WrongIdentity=false;
            provider.Unavailable=true;
            await Check(client,"/api/admin/data",403);
            provider.Unavailable=false;
            provider.Oversized=true;
            await Check(client,"/api/admin/data",403);
            provider.Oversized=false;
            builder.Configuration["Discord:DeveloperRoleId"]="666666666666666666";
            await Check(client, "/api/admin/data", 403);
            builder.Configuration["Discord:DeveloperRoleId"]="555555555555555555";
            builder.Configuration["Discord:BotToken"]="";
            await Check(client, "/api/admin/data", 403);
            builder.Configuration["Discord:BotToken"]="test-bot-token";
            await using (var unlink = database.CreateCommand("DELETE FROM gc.external_identities WHERE user_id=$1"))
            { unlink.Parameters.AddWithValue(account); await unlink.ExecuteNonQueryAsync(); }
            await Check(client, "/api/admin/data", 403);
            // Restore through the same verified provider flow, not an identity-write route.
            client.DefaultRequestHeaders.Remove("Cookie");
            administrator = await Login(client, "admin");
            client.DefaultRequestHeaders.Add("Cookie", administrator);
            using var post = await client.PostAsync("/auth/logout", new StringContent(""));
            if ((int)post.StatusCode != 400) throw new Exception("Logout accepted without CSRF proof");
            using var csrfResponse = await client.GetAsync("/api/auth/csrf");
            using var csrf = JsonDocument.Parse(await csrfResponse.Content.ReadAsStringAsync());
            var cookies = administrator + "; " + string.Join("; ", csrfResponse.Headers.GetValues("Set-Cookie").Select(x => x.Split(';')[0]));
            client.DefaultRequestHeaders.Remove("Cookie"); client.DefaultRequestHeaders.Add("Cookie", cookies);
            using var logout = new HttpRequestMessage(HttpMethod.Post, "/auth/logout");
            logout.Headers.Add("X-CSRF-TOKEN", csrf.RootElement.GetProperty("token").GetString());
            using var logoutResponse = await client.SendAsync(logout);
            if ((int)logoutResponse.StatusCode != 204 || !logoutResponse.Headers.GetValues("Set-Cookie").Any(x => x.StartsWith("__Host-GC.Session=;")))
                throw new Exception("Logout did not clear session cookie");
            client.DefaultRequestHeaders.Remove("Cookie");
            await Check(client, "/api/admin/data", 401);
            // Re-login updates the display name and preserves the GC identity.
            var first = await Login(client, "member"); client.DefaultRequestHeaders.Add("Cookie", first);
            using var firstMe = JsonDocument.Parse(await client.GetStringAsync("/api/me"));
            client.DefaultRequestHeaders.Remove("Cookie"); provider.Name = "Renamed member";
            var second = await Login(client, "member"); client.DefaultRequestHeaders.Add("Cookie", second);
            using var secondMe = JsonDocument.Parse(await client.GetStringAsync("/api/me"));
            if (firstMe.RootElement.GetProperty("id").GetGuid() != secondMe.RootElement.GetProperty("id").GetGuid() ||
                secondMe.RootElement.GetProperty("name").GetString() != "Renamed member") throw new Exception("Discord re-login identity drift");
            provider.MemberDeveloper=true;
            await Check(client,"/api/admin/data",200); // Role grant works for another account without a website allowlist.
            provider.RoleStatus=HttpStatusCode.TooManyRequests;
            await Check(client,"/api/admin/data",403);
            var calls=provider.RoleCalls;
            provider.RoleStatus=HttpStatusCode.OK;
            await Check(client,"/api/admin/data",403);
            if(provider.RoleCalls!=calls)throw new Exception("Discord Retry-After not respected");
            Console.WriteLine("PASS: OAuth code flow, secure session, stable identity, Developer role grants/removal, legacy allowlist ignored, provider failure/rate-limit denial, ordinary sign-in isolation and CSRF logout");
        }
        finally { await app.StopAsync(); if (Directory.Exists(keys)) Directory.Delete(keys, true); }
    }

    private static async Task Check(HttpClient client, string path, int expected)
    {
        using var response = await client.GetAsync(path);
        if ((int)response.StatusCode != expected) throw new Exception($"Auth check {path}: expected {expected}, got {(int)response.StatusCode}");
    }

    private static async Task<string> Login(HttpClient client, string code)
    {
        using var start = await client.GetAsync("/auth/discord");
        var location = start.Headers.Location ?? throw new Exception("Missing Discord redirect");
        var query = QueryHelpers.ParseQuery(location.Query);
        if (location.Host != "discord.com" || query["scope"] != "identify" || query["code_challenge_method"] != "S256")
            throw new Exception("OAuth challenge scope/PKCE mismatch");
        var correlation = string.Join("; ", start.Headers.GetValues("Set-Cookie").Select(x => x.Split(';')[0]));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/auth/discord/callback?code=" + code + "&state=" + Uri.EscapeDataString(query["state"].ToString()) + "&iss=https%3A%2F%2Fdiscord.com");
        request.Headers.Add("Cookie", correlation);
        using var response = await client.SendAsync(request);
        if (response.StatusCode != HttpStatusCode.Redirect || response.Headers.Location?.OriginalString != "/account.html")
            throw new Exception("OAuth callback failed: " + await response.Content.ReadAsStringAsync());
        var cookie = response.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("__Host-GC.Session="));
        if (!cookie.Contains("secure", StringComparison.OrdinalIgnoreCase) || !cookie.Contains("httponly", StringComparison.OrdinalIgnoreCase))
            throw new Exception("Session cookie protections missing");
        return cookie.Split(';')[0];
    }

    private sealed class DiscordStub : HttpMessageHandler
    {
        public string Name { get; set; } = "Test member";
        public bool Developer { get; set; } = true;
        public bool MemberDeveloper { get; set; }
        public bool MalformedRoles { get; set; }
        public bool WrongIdentity { get; set; }
        public bool Unavailable { get; set; }
        public bool Oversized { get; set; }
        public HttpStatusCode RoleStatus { get; set; } = HttpStatusCode.OK;
        public int RoleCalls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if(request.RequestUri?.AbsolutePath.StartsWith("/api/v10/guilds/",StringComparison.Ordinal)==true)
            {
                RoleCalls++;
                if(Unavailable)throw new HttpRequestException("Simulated provider outage");
                if(Oversized)return Json(new{extra=new string('x',128*1024)});
                if(!request.RequestUri.AbsolutePath.StartsWith("/api/v10/guilds/444444444444444444/members/",StringComparison.Ordinal)
                    ||request.Headers.Authorization?.ToString()!="Bot test-bot-token")throw new Exception("Invalid role lookup boundary");
                if(RoleStatus!=HttpStatusCode.OK){var response=new HttpResponseMessage(RoleStatus);response.Headers.RetryAfter=new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));return response;}
                if(MalformedRoles)return Json(new{roles="bad"});
                var id=request.RequestUri.Segments.Last();
                return Json(new{user=new{id=WrongIdentity?"999999999999999999":id},roles=(id=="222222222222222222"?Developer:MemberDeveloper)?new[]{"555555555555555555"}:new[]{"777777777777777777"}});
            }
            if (request.RequestUri?.AbsoluteUri == "https://discord.com/api/oauth2/token")
            {
                var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(token));
                if (form["client_secret"] != "test-secret" || string.IsNullOrWhiteSpace(form["code_verifier"]) || form["grant_type"] != "authorization_code")
                    throw new Exception("Invalid code exchange");
                return Json(new { access_token = form["code"].ToString(), token_type = "Bearer", expires_in = 3600 });
            }
            if (request.RequestUri?.AbsoluteUri != "https://discord.com/api/users/@me") throw new Exception("Unexpected provider request");
            return Json(new { id = request.Headers.Authorization?.Parameter == "admin" ? "222222222222222222" : "333333333333333333", username = Name });
        }
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}
