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
    private static async Task CheckNavigation(HttpClient client, bool account, bool admin)
    {
        using var response = await client.GetAsync("/api/navigation");
        response.EnsureSuccessStatusCode();
        using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = data.RootElement.EnumerateArray().Select(x => x.GetProperty("href").GetString()).ToArray();
        if (!paths.Contains("/") || paths.Contains("/account.html") != account || paths.Contains("/admin") != admin
            || !paths.Contains("/aar") || paths.Intersect(new[] { "/missions", "/players", "/sessions", "/playback", "/admin/data" }).Any()
            || response.Headers.CacheControl?.NoStore != true) throw new Exception("Navigation permission filtering failed");
    }
    public static async Task Run(NpgsqlDataSource database, bool steamOnly = false)
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
        var roleClock = new DiscordRoleChecks.Clock();
        builder.Services.AddSingleton(sp => new DiscordDeveloperAccess(builder.Configuration, sp.GetRequiredService<IHttpClientFactory>(), roleClock));
        var provider = new DiscordStub();
        var steamProvider = new SteamStub();
        var patreonProvider = new PatreonStub();
        builder.Services.AddHttpClient("patreon").ConfigurePrimaryHttpMessageHandler(() => patreonProvider);
        builder.Services.AddHttpClient("steam-openid").ConfigurePrimaryHttpMessageHandler(() => steamProvider);
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
            app.Configuration["Steam:PublicOrigin"]="https://"+new Uri(address).Authority;
            app.Configuration["Patreon:PublicOrigin"]=app.Configuration["Steam:PublicOrigin"];
            app.Configuration["Patreon:ClientId"]="test-patreon";app.Configuration["Patreon:ClientSecret"]="test-patreon-secret";app.Configuration["Patreon:CampaignId"]="777";
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = new Uri(address) };
            await Check(client, "/api/admin/data", 401);
            await Login(client, "member", "/account.html");
            await Login(client, "member", "/playback?mission=example");
            await Check(client, "/api/admin/recordings", 401);
            await Check(client, "/api/account/settings", 401);
            await Check(client,"/api/account/patreon",401);
            await Check(client, "/api/admin/accounts", 401);
            await CheckNavigation(client, false, false);
            await Check(client, "/auth/discord/callback?code=admin&state=forged", 400);
            var member = await Login(client, "member");
            client.DefaultRequestHeaders.Add("Cookie", member);
            await AccountSettingsChecks.Run(client);
            await SteamChecks.Run(client,database,steamProvider);
            using(var scope=app.Services.CreateScope()) await PatreonChecks.Run(client,database,patreonProvider,scope.ServiceProvider.GetRequiredService<PatreonConnections>());
            if(steamOnly)return;
            await CheckNavigation(client, true, false);
            await Check(client, "/api/admin/data", 403);
            foreach (var page in new[] { "/admin", "/admin/data", "/playback" })
            {
                using var denied = await client.GetAsync(page);
                if (denied.StatusCode != HttpStatusCode.Redirect || denied.Headers.Location?.OriginalString != "/insufficient-permissions")
                    throw new Exception("Missing permissions page for " + page);
            }
            using (var notice = await client.GetAsync("/insufficient-permissions"))
                if (!notice.IsSuccessStatusCode || !(await notice.Content.ReadAsStringAsync()).Contains("Insufficient permissions"))
                    throw new Exception("Insufficient permissions page missing");
            await Check(client, "/api/admin/recordings", 403);
            await Check(client, "/api/admin/accounts", 403);
            await Check(client, "/api/admin/tables/users", 403);
            await Check(client, $"/api/admin/missions/{Guid.NewGuid()}", 403);
            await Check(client, $"/api/admin/recordings/{Guid.NewGuid()}/{Guid.NewGuid()}/records?fromMs=0&untilMs=1000", 403);
            client.DefaultRequestHeaders.Remove("Cookie");
            var administrator = await Login(client, "admin");
            client.DefaultRequestHeaders.Add("Cookie", administrator);
            using(var settings=JsonDocument.Parse(await client.GetStringAsync("/api/account/settings")))
                if(settings.RootElement.GetProperty("videoVolume").GetInt32()!=25)throw new Exception("Preferences leaked across accounts");
            using var me = JsonDocument.Parse(await client.GetStringAsync("/api/me"));
            var account = me.RootElement.GetProperty("id").GetGuid();
            if (!me.RootElement.GetProperty("isAdmin").GetBoolean() || me.RootElement.GetProperty("discordId").GetString() != "222222222222222222")
                throw new Exception("Verified Discord account not reflected in profile");
            await Check(client, "/api/admin/data", 200);
            await Check(client, "/admin", 200);
            await Check(client, "/playback", 200);
            await Check(client, "/api/admin/recordings", 200);
            await Check(client, "/api/admin/recordings?offset=-1", 400);
            await using (var seed = database.CreateCommand("""
                INSERT INTO gc.telemetry_batches(server_id,recording_id,mission_id,revision,header,from_ms,until_ms,record_count,first_sequence,closed,dropped_records,type_counts,spawned_entities)
                SELECT '00000000-0000-0000-0000-000000000001'::uuid,gen_random_uuid(),gen_random_uuid(),0,
                    '{"world":"test-world","startedAt":1793637726}',0,1000,10,1,true,0,'{}','{}'
                FROM generate_series(1,51)
                """)) await seed.ExecuteNonQueryAsync();
            using (var page = JsonDocument.Parse(await client.GetStringAsync("/api/admin/recordings")))
            {
                var rows = page.RootElement.GetProperty("recordings");
                if (rows.GetArrayLength() != 50 || page.RootElement.GetProperty("nextOffset").GetInt32() != 50
                    || rows[0].GetProperty("recordCount").GetInt64() != 10 || rows[0].GetProperty("world").GetString() != "test-world")
                    throw new Exception("Playback recording directory fields/pagination failed");
            }
            using (var page = JsonDocument.Parse(await client.GetStringAsync("/api/admin/recordings?offset=50")))
                if (page.RootElement.GetProperty("recordings").GetArrayLength() != 1 || page.RootElement.GetProperty("nextOffset").ValueKind != JsonValueKind.Null)
                    throw new Exception("Playback final directory page failed");
            Console.WriteLine("PASS: Playback page and recording directory enforce Developer access and pagination");
            await Check(client, "/api/admin/accounts", 200);
            await Check(client, "/api/admin/accounts?offset=-1", 400);
            await Check(client, "/api/admin/tables/users", 200);
            await Check(client, "/api/admin/tables/not_allowed", 404);
            using(var accounts=JsonDocument.Parse(await client.GetStringAsync("/api/admin/accounts")))
                if(accounts.RootElement.GetProperty("accounts").GetArrayLength()<2)throw new Exception("Admin accounts list missing identities");
            await using(var seed=database.CreateCommand("INSERT INTO gc.users(id) SELECT gen_random_uuid() FROM generate_series(1,51)")) await seed.ExecuteNonQueryAsync();
            using(var page=JsonDocument.Parse(await client.GetStringAsync("/api/admin/accounts")))
                if(page.RootElement.GetProperty("accounts").GetArrayLength()!=50 || page.RootElement.GetProperty("nextOffset").GetInt32()!=50)throw new Exception("Account pagination failed");
            using(var page=JsonDocument.Parse(await client.GetStringAsync("/api/admin/tables/users?offset=50")))
                if(page.RootElement.GetProperty("rows").GetArrayLength()==0)throw new Exception("Table pagination failed");
            Console.WriteLine("PASS: Admin account directory and table browser enforce access, allowlists and pagination");
            await Check(client, $"/api/admin/recordings/{Guid.NewGuid()}/{Guid.NewGuid()}", 404);
            await CheckNavigation(client, true, true);
            provider.Developer = false;
            roleClock.Now += TimeSpan.FromMinutes(1);
            await CheckNavigation(client, true, false);
            await Check(client, "/api/admin/data", 403);
            provider.Developer = true;
            roleClock.Now += TimeSpan.FromMinutes(1);
            await Check(client, "/api/admin/data", 200);
            foreach(var failure in new[]{HttpStatusCode.NotFound,HttpStatusCode.Forbidden,HttpStatusCode.ServiceUnavailable})
            {
                provider.RoleStatus=failure;
                roleClock.Now += TimeSpan.FromMinutes(1);
                await Check(client, "/api/admin/data", 403);
                await Check(client, "/api/me", 200); // Provider trouble must not break ordinary login.
            }
            provider.RoleStatus=HttpStatusCode.OK;
            provider.MalformedRoles=true;
            roleClock.Now += TimeSpan.FromMinutes(1);
            await Check(client, "/api/admin/data", 403);
            provider.MalformedRoles=false;
            provider.WrongIdentity=true;
            roleClock.Now += TimeSpan.FromMinutes(1);
            await Check(client, "/api/admin/data", 403);
            provider.WrongIdentity=false;
            provider.Unavailable=true;
            roleClock.Now += TimeSpan.FromMinutes(1);
            await Check(client,"/api/admin/data",403);
            provider.Unavailable=false;
            provider.Oversized=true;
            roleClock.Now += TimeSpan.FromMinutes(1);
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
            using(var settings=JsonDocument.Parse(await client.GetStringAsync("/api/account/settings")))
                if(settings.RootElement.GetProperty("videoVolume").GetInt32()!=37)throw new Exception("Preferences lost after sign-in");
            using var secondMe = JsonDocument.Parse(await client.GetStringAsync("/api/me"));
            if (firstMe.RootElement.GetProperty("id").GetGuid() != secondMe.RootElement.GetProperty("id").GetGuid() ||
                secondMe.RootElement.GetProperty("name").GetString() != "Renamed member") throw new Exception("Discord re-login identity drift");
            provider.MemberDeveloper=true;
            roleClock.Now += TimeSpan.FromMinutes(1);
            await Check(client,"/api/admin/data",200); // Role grant works for another account without a website allowlist.
            provider.RoleStatus=HttpStatusCode.TooManyRequests;
            roleClock.Now += TimeSpan.FromMinutes(1);
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

    private static async Task<string> Login(HttpClient client, string code, string page = "/auth/discord")
    {
        using var start = await client.GetAsync(page);
        var location = start.Headers.Location ?? throw new Exception("Missing Discord redirect");
        var query = QueryHelpers.ParseQuery(location.Query);
        if (location.Host != "discord.com" || query["scope"] != "identify" || query["code_challenge_method"] != "S256")
            throw new Exception("OAuth challenge scope/PKCE mismatch");
        var correlation = string.Join("; ", start.Headers.GetValues("Set-Cookie").Select(x => x.Split(';')[0]));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/auth/discord/callback?code=" + code + "&state=" + Uri.EscapeDataString(query["state"].ToString()) + "&iss=https%3A%2F%2Fdiscord.com");
        request.Headers.Add("Cookie", correlation);
        using var response = await client.SendAsync(request);
        if (response.StatusCode != HttpStatusCode.Redirect || response.Headers.Location?.OriginalString != (page == "/auth/discord" ? "/account.html" : page))
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
