using GC.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;

internal static class MapPackageHttpChecks
{
    public static async Task Run(string package)
    {
        Check(MapPackages.MatchesWorld("{GUID}Worlds\\Custom\\Island.ent", "worlds/custom/island.ent"), "World matching normalizes separators and case");
        Check(!MapPackages.MatchesWorld("Worlds/Other.ent", "worlds/custom/island.ent") && !MapPackages.MatchesWorld("", ""), "Unknown worlds never match an empty suffix");
        var root=Path.Combine(Path.GetTempPath(),"gc-map-http-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);await File.WriteAllTextAsync(Path.Combine(root,"playback-maps.json"),"[]");
        try
        {
            var builder=WebApplication.CreateBuilder(new WebApplicationOptions { WebRootPath=root });
            builder.Logging.ClearProviders();
            builder.Configuration["Maps:StoragePath"]=Path.GetRelativePath(Environment.CurrentDirectory,Path.Combine(root,"storage"));
            builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions,TestMember>("test",_=>{});
            builder.Services.AddAuthorization(o=>o.AddPolicy(SiteNavigation.AdminPolicy,p=>p.RequireRole("Developer")));
            builder.Services.AddAntiforgery(o=>o.HeaderName="X-CSRF-TOKEN");
            builder.Services.AddRateLimiter(o=>o.AddPolicy("intake",_=>System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter("test")));
            await using var app=builder.Build();
            app.UseAuthentication();app.UseAuthorization();app.UseRateLimiter();MapPackages.Map(app);
            app.MapGet("/test/csrf",(Microsoft.AspNetCore.Http.HttpContext ctx,IAntiforgery csrf)=>new { token=csrf.GetAndStoreTokens(ctx).RequestToken }).RequireAuthorization();
            app.Urls.Add("http://127.0.0.1:0");await app.StartAsync();
            using var client=new HttpClient(new HttpClientHandler { CookieContainer=new CookieContainer() }) { BaseAddress=new Uri(app.Urls.Single()) };
            Check((await client.GetAsync("/api/admin/maps")).StatusCode==HttpStatusCode.Unauthorized,"Anonymous map admin is denied");
            Check((await client.GetAsync("/api/admin/maps/missing")).StatusCode==HttpStatusCode.Unauthorized,"Anonymous missing-map listing is denied");
            client.DefaultRequestHeaders.Add("X-Test-Member","Member");
            Check((await client.GetAsync("/api/admin/maps")).StatusCode==HttpStatusCode.Forbidden,"Ordinary member cannot administer maps");
            Check((await client.GetAsync("/api/admin/maps/missing")).StatusCode==HttpStatusCode.Forbidden,"Ordinary member cannot view missing maps");
            client.DefaultRequestHeaders.Remove("X-Test-Member");client.DefaultRequestHeaders.Add("X-Test-Member","Developer");
            Check((await client.GetAsync("/api/admin/maps/missing")).StatusCode==HttpStatusCode.ServiceUnavailable,"Missing database is not reported as all maps present");
            using (var body=new ByteArrayContent([])) Check((await client.PostAsync("/api/admin/maps",body)).StatusCode==HttpStatusCode.BadRequest,"Upload requires CSRF token");
            var csrf=await client.GetFromJsonAsync<JsonObject>("/test/csrf");client.DefaultRequestHeaders.Add("X-CSRF-TOKEN",(string?)csrf!["token"]);
            using var upload=new StreamContent(File.OpenRead(package));
            var result=await client.PostAsync("/api/admin/maps",upload);
            Check(result.IsSuccessStatusCode,"Developer uploads real package over HTTP");
            var maps=await client.GetFromJsonAsync<JsonArray>("/api/playback/maps") ?? throw new InvalidDataException("Missing playback registry");
            using var archive=System.IO.Compression.ZipFile.OpenRead(package);
            using var manifestStream=archive.GetEntry("manifest.json")!.Open();
            var config=(await JsonNode.ParseAsync(manifestStream))!["map"]!;
            var expectedWorlds=new HashSet<string>(StringComparer.OrdinalIgnoreCase) { (string)config["worldSuffix"]! };
            if (config["worldAliases"] is JsonArray aliases)
                foreach (var alias in aliases) expectedWorlds.Add((string)alias!);
            Check(expectedWorlds.SetEquals(maps!.Select(m=>(string)m!["worldSuffix"]!)) && maps.Count==expectedWorlds.Count,"Playback discovers each verified recording world");
            Check(maps.Select(m=>(string)m!["image"]!).Distinct().Count()==1,"Recording worlds share one installed asset set");
            var image=(string)maps[0]!["image"]!;
            Check((await client.GetAsync(image)).IsSuccessStatusCode,"Relative configured storage serves image successfully");
            var catalog=await client.GetFromJsonAsync<JsonObject>((string)maps[0]!["buildings"]!);
            var preview=catalog!["models"]!.AsObject().Select(p=>(string?)p.Value!["image"]).First(p=>p is not null)!;
            Check((await client.GetAsync(preview)).IsSuccessStatusCode,"Shared preview is served");
            client.DefaultRequestHeaders.Remove("X-Test-Member");
            Check((await client.GetAsync(preview)).StatusCode==HttpStatusCode.Unauthorized,"Shared preview requires authentication");
            Check((await client.GetAsync(image)).StatusCode==HttpStatusCode.Unauthorized,"Uploaded assets retain restricted access");
            await app.StopAsync();
        }
        finally { Directory.Delete(root,true); }
    }
    private static void Check(bool value,string message) { if(!value)throw new Exception(message);Console.WriteLine("PASS: "+message); }
    // Test-host-only identity. Never registered in the production application.
    private sealed class TestMember(IOptionsMonitor<AuthenticationSchemeOptions> options,ILoggerFactory logger,UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options,logger,encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role=Request.Headers["X-Test-Member"].ToString();
            return Task.FromResult(string.IsNullOrEmpty(role) ? AuthenticateResult.NoResult() : AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name,"Fixture"),new Claim(ClaimTypes.Role,role)],"test")),"test")));
        }
    }
}
