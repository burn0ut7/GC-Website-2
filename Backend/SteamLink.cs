using GC.Infrastructure;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.WebUtilities;
using Npgsql;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace GC.Web;

public static class SteamLink
{
    public const string Endpoint = "https://steamcommunity.com/openid/login";
    private const string Cookie = "__Host-GC.SteamLink";
    public static string? Origin(IConfiguration config)
    {
        var value=config["Steam:PublicOrigin"];
        return Uri.TryCreate(value,UriKind.Absolute,out var uri) && uri.Scheme=="https" && uri.AbsolutePath=="/"
            && uri.Query=="" && uri.Fragment=="" && uri.UserInfo=="" ? uri.GetLeftPart(UriPartial.Authority) : null;
    }
    public static void Configure(IServiceCollection services) => services.AddHttpClient("steam-openid", c =>
    { c.Timeout=TimeSpan.FromSeconds(10); c.MaxResponseContentBufferSize=16384; })
        .ConfigurePrimaryHttpMessageHandler(()=>new HttpClientHandler { AllowAutoRedirect=false });

    // Verify the exact return address and every identity-bearing signed field before contacting Steam.
    public static string? ClaimedSteamId(IReadOnlyDictionary<string,string> fields,string returnTo,DateTimeOffset now)
    {
        string Get(string name)=>fields.GetValueOrDefault("openid."+name)??"";
        if(Get("ns")!="http://specs.openid.net/auth/2.0" || Get("mode")!="id_res"
            || Get("sig").Length==0 || Get("assoc_handle").Length==0 || Get("op_endpoint")!=Endpoint || Get("return_to")!=returnTo || Get("identity")!=Get("claimed_id")) return null;
        var signed=Get("signed").Split(',').ToHashSet(StringComparer.Ordinal);
        if(new[]{"op_endpoint","claimed_id","identity","return_to","response_nonce","assoc_handle"}.Any(x=>!signed.Contains(x))) return null;
        const string prefix="https://steamcommunity.com/openid/id/";
        var claimed=Get("claimed_id");
        if(!claimed.StartsWith(prefix,StringComparison.Ordinal) || !SteamAccounts.ValidId(claimed[prefix.Length..])) return null;
        var nonce=Get("response_nonce");
        if(nonce.Length<=20 || !DateTimeOffset.TryParseExact(nonce[..20],"yyyy-MM-dd'T'HH:mm:ss'Z'",CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,out var time) || Math.Abs((now-time).TotalMinutes)>5) return null;
        return claimed[prefix.Length..];
    }

    public static void Map(WebApplication app)
    {
        var group=app.MapGroup("/api/account/steam").RequireAuthorization();
        group.AddEndpointFilter(async (context,next)=>
        {
            context.HttpContext.Response.Headers.CacheControl="no-store";
            var db=context.HttpContext.RequestServices.GetService<NpgsqlDataSource>();
            if(db is null)return Results.StatusCode(503);
            if(!Guid.TryParse(context.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier),out var user)
                || await Accounts.DiscordIdentity(db,user,context.HttpContext.RequestAborted) is null)return Results.Unauthorized();
            return await next(context);
        });
        group.MapGet("",async (HttpContext http,[Microsoft.AspNetCore.Mvc.FromServices] NpgsqlDataSource db)=>Results.Ok(new {
            enabled=Origin(app.Configuration) is not null,
            connection=await SteamAccounts.Read(db,User(http),http.RequestAborted) }));
        group.MapPost("/link",async (HttpContext http,[Microsoft.AspNetCore.Mvc.FromServices] NpgsqlDataSource db,IAntiforgery csrf)=>
        {
            if(!await Csrf(http,csrf))return Results.BadRequest();
            var origin=Origin(app.Configuration);
            if(origin is null)return Results.Json(new {error="steam_not_configured"},statusCode:503);
            if(!http.Request.IsHttps || $"{http.Request.Scheme}://{http.Request.Host}"!=origin)return Results.BadRequest(new {error="use_configured_site_address"});
            var state=WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            await using var command=db.CreateCommand("WITH cleanup AS (DELETE FROM gc.steam_link_challenges WHERE user_id=$1 OR expires_at<now()) INSERT INTO gc.steam_link_challenges(state_hash,user_id,expires_at) VALUES($2,$1,now()+interval '5 minutes')");
            command.Parameters.AddWithValue(User(http));command.Parameters.AddWithValue(Hash(state));await command.ExecuteNonQueryAsync(http.RequestAborted);
            http.Response.Cookies.Append(Cookie,state,new CookieOptions { Secure=true,HttpOnly=true,SameSite=SameSiteMode.Lax,Path="/",MaxAge=TimeSpan.FromMinutes(5) });
            var address=QueryHelpers.AddQueryString(Endpoint,new Dictionary<string,string?> {
                ["openid.ns"]="http://specs.openid.net/auth/2.0",["openid.mode"]="checkid_setup",
                ["openid.claimed_id"]="http://specs.openid.net/auth/2.0/identifier_select",
                ["openid.identity"]="http://specs.openid.net/auth/2.0/identifier_select",
                ["openid.realm"]=origin+"/",["openid.return_to"]=ReturnTo(origin,state) });
            return Results.Ok(new {url=address});
        });
        group.MapDelete("",async (HttpContext http,[Microsoft.AspNetCore.Mvc.FromServices] NpgsqlDataSource db,IAntiforgery csrf)=>
        {
            if(!await Csrf(http,csrf))return Results.BadRequest();
            await SteamAccounts.Disconnect(db,User(http),http.RequestAborted);
            return Results.NoContent();
        });
        app.MapGet("/auth/steam/callback",async (HttpContext http,[Microsoft.AspNetCore.Mvc.FromServices] NpgsqlDataSource db,IHttpClientFactory clients)=>
        {
            IResult Finish(string result)=>Results.Redirect("/account.html?steam="+result+"#connections");
            var origin=Origin(app.Configuration);
            var state=http.Request.Query["state"].ToString();
            if(origin is null || !http.Request.IsHttps || state.Length!=43 || http.Request.Cookies[Cookie]!=state
                || http.Request.Query.Any(p=>p.Value.Count!=1 || p.Value.ToString().Length>2048))return Finish("failed");
            http.Response.Cookies.Delete(Cookie,new CookieOptions{Secure=true,HttpOnly=true,Path="/"});
            // Check eligibility now; consume atomically with linking after Steam verification.
            await using var consume=db.CreateCommand("SELECT user_id FROM gc.steam_link_challenges WHERE state_hash=$1 AND user_id=$2 AND expires_at>now()");
            consume.Parameters.AddWithValue(Hash(state));consume.Parameters.AddWithValue(User(http));
            if(await consume.ExecuteScalarAsync(http.RequestAborted) is not Guid)return Finish("failed");
            if(http.Request.Query["openid.mode"]=="cancel")return Finish("cancelled");
            var fields=http.Request.Query.Where(p=>p.Key.StartsWith("openid.",StringComparison.Ordinal)).ToDictionary(p=>p.Key,p=>p.Value.ToString());
            var steam=ClaimedSteamId(fields,ReturnTo(origin,state),DateTimeOffset.UtcNow);
            if(steam is null)return Finish("failed");
            fields["openid.mode"]="check_authentication";
            try {
                using var response=await clients.CreateClient("steam-openid").PostAsync(Endpoint,new FormUrlEncodedContent(fields),http.RequestAborted);
                if(!response.IsSuccessStatusCode)return Finish("failed");
                var body=await response.Content.ReadAsStringAsync(http.RequestAborted);
                if(!body.Split('\n').Select(l=>l.TrimEnd('\r')).Contains("is_valid:true",StringComparer.Ordinal))return Finish("failed");
                return Finish(await SteamAccounts.Complete(db,User(http),Hash(state),steam,http.RequestAborted));
            } catch(Exception error) when(error is HttpRequestException or TaskCanceledException) { return Finish("failed"); }
        }).RequireAuthorization();
    }
    private static Guid User(HttpContext http)=>Guid.Parse(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private static string Hash(string state)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state)));
    private static string ReturnTo(string origin,string state)=>origin+"/auth/steam/callback?state="+state;
    private static async Task<bool> Csrf(HttpContext http,IAntiforgery csrf) { try {await csrf.ValidateRequestAsync(http);return true;} catch(AntiforgeryValidationException){return false;} }
}
