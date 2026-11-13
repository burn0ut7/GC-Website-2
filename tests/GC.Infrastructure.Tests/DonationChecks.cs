using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using GC.Infrastructure;
using GC.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

internal static class DonationChecks
{
    private static void Check(bool pass, string message) { if (!pass) throw new Exception(message); Console.WriteLine("PASS: " + message); }
    private static string Member(string subject, long? amount = 500, int cadence = 1, string status = "active_patron", string charge = "Paid") => JsonSerializer.Serialize(new {
        type = "member", id = "member-" + subject, attributes = new { patron_status = status, last_charge_status = charge,
            currently_entitled_amount_cents = 500, will_pay_amount_cents = amount, pledge_cadence = cadence.ToString(), pledge_relationship_start = "2026-01-01T00:00:00Z" },
        relationships = new { user = new { data = new { type = "user", id = subject } } }
    });
    private static DonationSync.Member? Parse(string json) { using var doc = JsonDocument.Parse(json); return DonationSync.ParseMember(doc.RootElement); }
    public static async Task Run(NpgsqlDataSource db)
    {
        Check(Parse(Member("101", 12000, 12))?.MonthlyCents == 1000, "Annual pledges normalized to monthly support");
        Check(Parse(Member("101", null))?.MonthlyCents is null, "Missing amount remains unknown");
        Check(Parse(Member("101", status: "declined_patron")) is null && Parse(Member("101", charge: "Declined")) is null,
            "Declined support excluded");
        var user = await Accounts.SignInDiscord(db, "7654321001", "Visible supporter", default, new string('a',32));
        var other = await Accounts.SignInDiscord(db, "7654321002", "Second supporter", default);
        await using (var command = db.CreateCommand("INSERT INTO gc.external_identities(provider,subject,user_id,display_name) VALUES('patreon','101',$1,'private patreon name'),('patreon','102',$2,'private second name')"))
        { command.Parameters.AddWithValue(user); command.Parameters.AddWithValue(other); await command.ExecuteNonQueryAsync(); }
        Check(await DonationRoutes.ReadPrivacy(db,user,default) == new DonationRoutes.Privacy(), "Privacy defaults to named with hidden amount");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
            ["Patreon:CampaignId"]="999",["Patreon:CreatorAccessToken"]="test-only-token"
        }).Build();
        var provider = new Provider();
        using var client = new HttpClient(provider);
        await DonationSync.Refresh(db,client,config,default);
        var snapshot = await DonationRoutes.Read(db,"999",default);
        Check(provider.Requests == 3 && snapshot.SupporterCount == 3 && snapshot.MonthlyCents == 2000, "All Patreon pages included, including unlinked support");
        Check(snapshot.Donors.Count == 2 && snapshot.AnonymousCount == 1 && snapshot.Donors.All(x=>x.MonthlyCents is null), "Default amounts absent from public DTO");
        Check(snapshot.Donors.Single(x=>x.Name=="Visible supporter").AvatarUrl!.Contains("cdn.discordapp.com/avatars/7654321001/"), "Verified Discord avatar used");
        Check(!JsonSerializer.Serialize(snapshot).Contains("private patreon name"), "Patreon names never exposed");
        await DonationRoutes.SavePrivacy(db,user,new(false,true),default);
        snapshot = await DonationRoutes.Read(db,"999",default);
        Check(snapshot.Donors.Single(x=>x.Name=="Visible supporter").MonthlyCents==500, "Amount appears only after opt-in");
        await DonationRoutes.SavePrivacy(db,user,new(true,true),default);
        snapshot = await DonationRoutes.Read(db,"999",default);
        Check(snapshot.AnonymousCount==2 && !JsonSerializer.Serialize(snapshot).Contains("Visible supporter")
            && !JsonSerializer.Serialize(snapshot).Contains("7654321001") && snapshot.MonthlyCents==2000, "Anonymous identity/avatar/history absent; aggregate retained");
        Check(!(await DonationRoutes.ReadPrivacy(db,user,default)).ShowAmount, "Anonymity overrides amount disclosure");
        provider.FailSecondPage=true;
        try { await DonationSync.Refresh(db,client,config,default); throw new Exception("Failure expected"); } catch(HttpRequestException) { }
        Check((await DonationRoutes.Read(db,"999",default)).MonthlyCents==2000,"Partial refresh never replaces complete snapshot");
        await using(var stale=db.CreateCommand("UPDATE gc.patreon_campaign_sync SET checked_at=now()-interval '2 hours' WHERE campaign_id='999'")) await stale.ExecuteNonQueryAsync();
        Check((await DonationRoutes.Read(db,"999",default)).Status=="stale","Old data explicitly marked stale");
        await DonationSync.Save(db,"999",[new("101",null,null)],default);
        Check((await DonationRoutes.Read(db,"999",default)).MonthlyCents is null,"Unknown amount never reported as zero total");
        await DonationSync.Save(db,"999",[],default);
        Check((await DonationRoutes.Read(db,"999",default)).MonthlyCents==0,"Complete empty campaign reports zero");
        await HttpChecks(db,user,other);
    }
    private static async Task HttpChecks(NpgsqlDataSource db,Guid user,Guid other)
    {
        var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(db);builder.Services.AddAuthentication("test").AddCookie("test");
        builder.Services.AddAuthorization();builder.Services.AddAntiforgery(o=>o.HeaderName="X-CSRF-TOKEN");
        await using var app=builder.Build();
        app.Use(async(http,next)=>{if(http.Request.Headers["Test-User"]==user.ToString())http.User=new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier,user.ToString())],"test"));await next();});
        app.UseAuthorization();DonationRoutes.Map(app);
        app.MapGet("/test-csrf",(Microsoft.AspNetCore.Http.HttpContext http,IAntiforgery csrf)=>csrf.GetAndStoreTokens(http).RequestToken);
        await app.StartAsync();
        using var client=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){BaseAddress=new Uri(app.Urls.Single())};
        Check((await client.GetAsync("/api/account/donations")).StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Redirect,"Privacy endpoint requires authentication");
        client.DefaultRequestHeaders.Add("Test-User",user.ToString());
        using(var response=await client.GetAsync("/api/account/donations"))
        {
            var body=await response.Content.ReadAsStringAsync();
            Check(response.IsSuccessStatusCode && body.Length>0,"Privacy GET returns a JSON body");
            using var json=JsonDocument.Parse(body);
            Check(json.RootElement.GetProperty("anonymous").ValueKind==JsonValueKind.True && json.RootElement.GetProperty("showAmount").ValueKind==JsonValueKind.False,"Privacy GET returns saved settings");
        }

        using(var response=await client.PutAsync("/api/account/donations",new StringContent("{}",Encoding.UTF8,"application/json")))
            Check(response.StatusCode==HttpStatusCode.BadRequest,"Privacy changes reject missing CSRF");
        var csrf=await client.GetStringAsync("/test-csrf");client.DefaultRequestHeaders.Add("X-CSRF-TOKEN",csrf);
        using(var response=await client.PutAsync("/api/account/donations",new StringContent(JsonSerializer.Serialize(new{anonymous=false,showAmount=true,userId=other}),Encoding.UTF8,"application/json")))
            Check(response.IsSuccessStatusCode,"Authenticated CSRF-protected privacy save succeeds");
        Check((await DonationRoutes.ReadPrivacy(db,user,default)).ShowAmount && !(await DonationRoutes.ReadPrivacy(db,other,default)).ShowAmount,"Caller cannot modify another account");
        using(var response=await client.GetAsync("/api/donations")) { var text=await response.Content.ReadAsStringAsync();Check(response.IsSuccessStatusCode && response.Headers.CacheControl?.NoStore==true && text.Contains("not_configured") && text.Contains("10000"),"Unconfigured public page has $100 estimate and no invented totals"); }
        await app.StopAsync();
    }
    private sealed class Provider : HttpMessageHandler
    {
        public int Requests;public bool FailSecondPage;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            Requests++;
            var second=request.RequestUri!.Query.Contains("cursor");
            if(FailSecondPage&&second)return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            var json=!request.RequestUri.AbsolutePath.EndsWith("/members")
                ? "{\"data\":{\"type\":\"campaign\",\"id\":\"999\",\"attributes\":{\"currency\":\"USD\",\"is_monthly\":true}}}"
                : second ? "{\"data\":["+Member("103",1000)+"],\"meta\":{\"pagination\":{\"cursors\":{\"next\":null}}}}"
                : "{\"data\":["+Member("101")+","+Member("102")+"],\"meta\":{\"pagination\":{\"cursors\":{\"next\":\"next-page\"}}}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json,Encoding.UTF8,"application/json")});
        }
    }
}
