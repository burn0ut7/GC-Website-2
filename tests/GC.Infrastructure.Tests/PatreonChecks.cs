using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using GC.Web;
using Microsoft.AspNetCore.WebUtilities;
using Npgsql;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;

internal sealed class PatreonStub : HttpMessageHandler
{
    public string Subject="12345", Campaign="777", Patron="active_patron", Charge="Paid";
    public int Amount=500, Calls, RefreshCalls;
    public int Expires=0;
    public bool Fail;
    public TaskCompletionSource? Entered, Release;
    public string Body()=>JsonSerializer.Serialize(new {
        data=new {type="user",id=Subject,relationships=new {memberships=new {data=new[]{new {type="member",id="m1"}}}}},
        included=new[]{new {type="member",id="m1",relationships=new {campaign=new {data=new {type="campaign",id=Campaign}}},attributes=new {patron_status=Patron,currently_entitled_amount_cents=Amount,last_charge_status=Charge}}}
    });
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
    {
        Calls++;
        if(request.RequestUri!.Host!="www.patreon.com")throw new Exception("Wrong Patreon destination");
        if(Entered is not null){Entered.TrySetResult();await Release!.Task.WaitAsync(token);}
        if(Fail)return new(HttpStatusCode.ServiceUnavailable);
        if(request.RequestUri.AbsolutePath.EndsWith("/token"))
        {
            var form=QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(token));
            if(request.Method!=HttpMethod.Post || form["client_secret"]!="test-patreon-secret")throw new Exception("Invalid token exchange");
            if(form["grant_type"]=="refresh_token"){RefreshCalls++;Expires=3600;}
            return new(HttpStatusCode.OK){Content=JsonContent.Create(new {access_token="secret-access",refresh_token="secret-refresh",expires_in=Expires})};
        }
        if(QueryHelpers.ParseQuery(request.RequestUri.Query)["include"]!="memberships.campaign")throw new Exception("Campaign relationship must be requested");
        if(request.Headers.Authorization?.Parameter!="secret-access")throw new Exception("No Patreon bearer token");
        return new(HttpStatusCode.OK){Content=new StringContent(Body())};
    }
}
internal static class PatreonChecks
{
    private static void Check(bool ok,string message){if(!ok)throw new Exception(message);Console.WriteLine("PASS: "+message);}
    public static async Task Run(HttpClient client,NpgsqlDataSource db,PatreonStub provider,PatreonConnections connector)
    {
        string Parse(){using var doc=JsonDocument.Parse(provider.Body());return PatreonClient.Parse(doc.RootElement,"777").Status;}
        Check(Parse()=="supporting","Paid target-campaign membership is supporting");
        provider.Campaign="888";Check(Parse()=="not_supporting","Other campaign does not grant support");provider.Campaign="777";
        provider.Amount=0;Check(Parse()=="not_supporting","Free membership is not paid support");provider.Amount=500;
        provider.Patron="declined_patron";Check(Parse()=="payment_issue","Declined membership distinguished");provider.Patron="former_patron";Check(Parse()=="not_supporting","Former membership distinguished");provider.Patron="active_patron";
        provider.Charge="Pending";Check(Parse()=="unverified","Pending payment is not confirmed support");provider.Charge="Paid";
        var original=string.Join("; ",client.DefaultRequestHeaders.GetValues("Cookie"));
        using var csrf=await client.GetAsync("/api/auth/csrf");var csrfToken=(await csrf.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        var cookies=original+"; "+string.Join("; ",csrf.Headers.GetValues("Set-Cookie").Select(x=>x.Split(';')[0]));
        void Cookies(string value){client.DefaultRequestHeaders.Remove("Cookie");client.DefaultRequestHeaders.Add("Cookie",value);}
        Cookies(cookies);
        async Task<string> Begin()
        {
            using var request=new HttpRequestMessage(HttpMethod.Post,"/api/account/patreon/link");request.Headers.Add("X-CSRF-TOKEN",csrfToken);
            using var response=await client.SendAsync(request);response.EnsureSuccessStatusCode();
            var url=(await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("url").GetString()!;
            Check(url.StartsWith("https://www.patreon.com/oauth2/authorize?"),"Fixed Patreon authorization destination");
            var query=QueryHelpers.ParseQuery(new Uri(url).Query);Check(query["scope"]=="identity identity.memberships","Only required identity/membership scopes");
            Cookies(cookies+"; "+string.Join("; ",response.Headers.GetValues("Set-Cookie").Select(x=>x.Split(';')[0])));
            return "/auth/patreon/callback?state="+query["state"]+"&code=test";
        }
        async Task<string> Callback(string url){using var response=await client.GetAsync(url);return response.Headers.Location?.OriginalString??"";}
        async Task Unlink(){using var request=new HttpRequestMessage(HttpMethod.Delete,"/api/account/patreon");request.Headers.Add("X-CSRF-TOKEN",csrfToken);Check((await client.SendAsync(request)).StatusCode==HttpStatusCode.NoContent,"Patreon disconnect succeeds");}
        try
        {
            Check((await client.PostAsync("/api/account/patreon/link",null)).StatusCode==HttpStatusCode.BadRequest,"Patreon link requires CSRF");
            Check((await client.DeleteAsync("/api/account/patreon")).StatusCode==HttpStatusCode.BadRequest,"Patreon unlink requires CSRF");
            Check((await Callback("/auth/patreon/callback?state=forged&code=test")).Contains("failed"),"Forged state rejected");
            var expired=await Begin();await using(var command=db.CreateCommand("UPDATE gc.patreon_link_challenges SET expires_at=now()-interval '1 minute'"))await command.ExecuteNonQueryAsync();
            Check((await Callback(expired)).Contains("failed"),"Expired Patreon state rejected");
            provider.Fail=true;Check((await Callback(await Begin())).Contains("failed"),"Provider failure does not link");provider.Fail=false;
            var delayed=await Begin();provider.Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);provider.Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending=Callback(delayed);await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));await Unlink();provider.Release.SetResult();Check((await pending).Contains("failed"),"Callback cannot undo disconnect");provider.Entered=null;provider.Release=null;
            var good=await Begin();Check((await Callback(good)).Contains("linked"),"Patreon OAuth completes");Check((await Callback(good)).Contains("failed"),"Patreon callback replay rejected");
            var status=await client.GetFromJsonAsync<JsonElement>("/api/account/patreon");Check(status.GetProperty("connection").GetProperty("supportStatus").GetString()=="supporting","Support status visible through account API");
            await using(var command=db.CreateCommand("SELECT protected_tokens FROM gc.patreon_connections")){var encrypted=(string)(await command.ExecuteScalarAsync())!;Check(!encrypted.Contains("secret-access")&&!encrypted.Contains("secret-refresh"),"Patreon tokens protected at rest");}
            var me=await client.GetFromJsonAsync<JsonElement>("/api/me");var user=me.GetProperty("id").GetGuid();
            async Task Due(){await using var command=db.CreateCommand("UPDATE gc.patreon_connections SET next_check_at=now()-interval '1 minute'");await command.ExecuteNonQueryAsync();}
            provider.Patron="former_patron";await Due();await connector.Refresh(user,default);Check((await connector.Read(user,default)).SupportStatus=="not_supporting","Refresh detects ended support");Check(provider.RefreshCalls==1,"Expired access token refreshes before membership verification");
            provider.Fail=true;await Due();await connector.Refresh(user,default);Check((await connector.Read(user,default)).SupportStatus=="unverified","API outage does not report not donating or preserve active status");provider.Fail=false;provider.Patron="active_patron";
            provider.Subject="99999";Check((await Callback(await Begin())).Contains("conflict"),"Cannot silently replace linked Patreon account");provider.Subject="12345";
            await Unlink();status=await client.GetFromJsonAsync<JsonElement>("/api/account/patreon");Check(!status.GetProperty("connection").GetProperty("connected").GetBoolean(),"Disconnect removes supporter connection");
        }
        finally{Cookies(original);}
    }
}
