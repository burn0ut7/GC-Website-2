using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using GC.Infrastructure;
using GC.Web;
using Microsoft.AspNetCore.WebUtilities;
using Npgsql;

internal sealed class SteamStub : HttpMessageHandler
{
    public bool Valid=true;
    public int Calls;
    public TaskCompletionSource? Entered;
    public TaskCompletionSource? Release;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
    {
        if(request.RequestUri?.AbsoluteUri!=SteamLink.Endpoint || request.Method!=HttpMethod.Post)throw new Exception("Steam verification wrong destination");
        var form=QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(token));
        if(form["openid.mode"]!="check_authentication")throw new Exception("No Steam signature verification");
        Calls++;
        if(Entered is not null){Entered.TrySetResult();await Release!.Task.WaitAsync(token);}
        return new(HttpStatusCode.OK){Content=new StringContent("ns:http://specs.openid.net/auth/2.0\nis_valid:"+(Valid?"true":"false")+"\n")};
    }
}
internal static class SteamChecks
{
    public static async Task Run(HttpClient client,NpgsqlDataSource db,SteamStub provider)
    {
        const string steam="76561198000000001";
        foreach(var invalid in new[]{"", "123", "76561198000000001x","76561197960265728","7656119800000000０","86561198000000001"})
            Check(!SteamAccounts.ValidId(invalid),"Invalid SteamID64 rejected");
        Check(SteamAccounts.ValidId(steam),"Individual SteamID64 accepted");
        var originalCookie=string.Join("; ",client.DefaultRequestHeaders.GetValues("Cookie"));
        using var csrf=await client.GetAsync("/api/auth/csrf");
        var csrfToken=(await csrf.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        var cookies=originalCookie+"; "+string.Join("; ",csrf.Headers.GetValues("Set-Cookie").Select(x=>x.Split(';')[0]));
        void SetCookie(string cookie){client.DefaultRequestHeaders.Remove("Cookie");client.DefaultRequestHeaders.Add("Cookie",cookie);}
        SetCookie(cookies);
        try {
            Check((await client.PostAsync("/api/account/steam/link",null)).StatusCode==HttpStatusCode.BadRequest,"Steam link requires CSRF");
            Check((await client.DeleteAsync("/api/account/steam")).StatusCode==HttpStatusCode.BadRequest,"Steam unlink requires CSRF");
            async Task<string> Begin()
            {
                using var request=new HttpRequestMessage(HttpMethod.Post,"/api/account/steam/link");request.Headers.Add("X-CSRF-TOKEN",csrfToken);
                using var response=await client.SendAsync(request);response.EnsureSuccessStatusCode();
                var url=(await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("url").GetString()!;
                var query=QueryHelpers.ParseQuery(new Uri(url).Query);
                Check(url.StartsWith(SteamLink.Endpoint+"?"),"Link redirects only to Steam");
                SetCookie(cookies+"; "+string.Join("; ",response.Headers.GetValues("Set-Cookie").Select(x=>x.Split(';')[0])));
                return query["openid.return_to"].ToString();
            }
            Dictionary<string,string?> Assertion(string returnTo)=>new(){
                ["openid.ns"]="http://specs.openid.net/auth/2.0",["openid.mode"]="id_res",["openid.op_endpoint"]=SteamLink.Endpoint,
                ["openid.return_to"]=returnTo,["openid.claimed_id"]="https://steamcommunity.com/openid/id/"+steam,
                ["openid.identity"]="https://steamcommunity.com/openid/id/"+steam,
                ["openid.response_nonce"]=DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")+"fixture",
                ["openid.assoc_handle"]="fixture",["openid.sig"]="fixture",
                ["openid.signed"]="op_endpoint,claimed_id,identity,return_to,response_nonce,assoc_handle"};
            async Task<string> Callback(string address,Dictionary<string,string?> fields)
            {
                using var response=await client.GetAsync(QueryHelpers.AddQueryString(new Uri(address).PathAndQuery,fields));
                return response.Headers.Location?.OriginalString??"no redirect";
            }
            foreach(var bad in new[]{"openid.op_endpoint","openid.return_to","openid.signed","openid.claimed_id","openid.response_nonce"}) {
                var address=await Begin();var fields=Assertion(address);fields[bad]="forged";var calls=provider.Calls;
                Check((await Callback(address,fields)).Contains("steam=failed")&&provider.Calls==calls,"Reject forged "+bad);
            }
            var denied=await Begin();provider.Valid=false;
            Check((await Callback(denied,Assertion(denied))).Contains("steam=failed"),"Reject invalid Steam signature");provider.Valid=true;
            var expired=await Begin();await using(var expire=db.CreateCommand("UPDATE gc.steam_link_challenges SET expires_at=now()-interval '1 minute'"))await expire.ExecuteNonQueryAsync();
            Check((await Callback(expired,Assertion(expired))).Contains("steam=failed"),"Expired challenge rejected");
            var delayed=await Begin();provider.Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);provider.Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending=Callback(delayed,Assertion(delayed));await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using(var disconnect=new HttpRequestMessage(HttpMethod.Delete,"/api/account/steam")){
                disconnect.Headers.Add("X-CSRF-TOKEN",csrfToken);Check((await client.SendAsync(disconnect)).StatusCode==HttpStatusCode.NoContent,"Disconnect during verification succeeds");}
            provider.Release.SetResult();Check((await pending).Contains("steam=failed"),"In-flight callback cannot undo disconnect");provider.Entered=null;provider.Release=null;
            var good=await Begin();var goodFields=Assertion(good);
            Check((await Callback(good,goodFields)).Contains("steam=linked"),"Verified Steam link succeeds");
            Check((await Callback(good,goodFields)).Contains("steam=failed"),"Callback replay rejected");
            var me=await client.GetFromJsonAsync<JsonElement>("/api/me");var user=me.GetProperty("id").GetGuid();
            var other=await Accounts.SignInDiscord(db,"999111222333444555","Other",default);
            Check(!await SteamAccounts.Link(db,other,steam,default),"Steam cannot link to two users");
            Check(!await SteamAccounts.Link(db,user,"76561198000000002",default),"User cannot silently replace Steam");
            Check(await SteamAccounts.Link(db,user,steam,default),"Repeated same link is idempotent");
            var uid=Guid.NewGuid();var mission=Guid.NewGuid();var server=Guid.NewGuid();
            var archive=Path.Combine(Path.GetTempPath(),"gc-steam-test-"+Guid.NewGuid().ToString("N"));
            try {
                var batch=JsonNode.Parse(PipelineChecks.Stats(mission,0))!;
                batch["players"]![0]!["identity"]=uid.ToString();batch["players"]![0]!["steamId"]=steam;
                await Intake.Receive(db,archive,server,"stats",mission,0,System.Text.Encoding.UTF8.GetBytes(batch.ToJsonString()),default);
                Check(await MissionProjection.ProjectNext(db,archive,default),"Server statistics projected");
                var resolved=await SteamAccounts.Resolve(db,uid,default);
                Check(resolved?.UserId==user && resolved.SteamId==steam && resolved.DiscordId==me.GetProperty("discordId").GetString(),"Server UID resolves to verified website + Discord + Steam");
                await using(var connection=await db.OpenConnectionAsync())await using(var tx=await connection.BeginTransactionAsync()){
                    await SteamAccounts.Observe(connection,tx,server,mission,uid.ToString(),"76561198000000002",default);await tx.CommitAsync();}
                Check(await SteamAccounts.Resolve(db,uid,default) is null,"Conflicting observation disables match");
                await using(var cleanup=db.CreateCommand("DELETE FROM gc.game_identity_observations WHERE identity=$1 AND steam_id<>$2")){cleanup.Parameters.AddWithValue(uid);cleanup.Parameters.AddWithValue(steam);await cleanup.ExecuteNonQueryAsync();}
                using var unlink=new HttpRequestMessage(HttpMethod.Delete,"/api/account/steam");unlink.Headers.Add("X-CSRF-TOKEN",csrfToken);
                Check((await client.SendAsync(unlink)).StatusCode==HttpStatusCode.NoContent,"Unlink succeeds");
                Check(await SteamAccounts.Resolve(db,uid,default) is null,"Unlink removes account resolution");
                await using(var history=db.CreateCommand("SELECT count(*) FROM gc.mission_players WHERE identity=$1")){history.Parameters.AddWithValue(uid.ToString());Check((long)(await history.ExecuteScalarAsync())! ==1,"Unlink preserves game history");}
            }finally{if(Directory.Exists(archive))Directory.Delete(archive,true);}
        }finally{SetCookie(originalCookie);}
        Console.WriteLine("PASS Steam authentication, identity projection, conflicts and disconnect");
    }
    private static void Check(bool value,string message){if(!value)throw new Exception(message);Console.WriteLine("PASS: "+message);}
}
