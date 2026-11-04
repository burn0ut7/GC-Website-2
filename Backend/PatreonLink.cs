using GC.Infrastructure;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.WebUtilities;
using Npgsql;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GC.Web;

public static class PatreonLink
{
    private const string Cookie = "__Host-GC.PatreonLink";
    public static void Configure(IServiceCollection services)
    {
        services.AddHttpClient("patreon", c => { c.Timeout=TimeSpan.FromSeconds(10); c.MaxResponseContentBufferSize=1024*1024; c.DefaultRequestHeaders.UserAgent.ParseAdd("GlobalConflicts-Website/1.0"); })
            .ConfigurePrimaryHttpMessageHandler(()=>new HttpClientHandler { AllowAutoRedirect=false });
        services.AddScoped<PatreonClient>();services.AddScoped<PatreonConnections>();services.AddHostedService<PatreonRefresh>();
    }
    public static void Map(WebApplication app)
    {
        var group=app.MapGroup("/api/account/patreon").RequireAuthorization();
        group.AddEndpointFilter(async (context,next)=>
        {
            context.HttpContext.Response.Headers.CacheControl="no-store";
            var db=context.HttpContext.RequestServices.GetService<NpgsqlDataSource>();
            if(db is null)return Results.StatusCode(503);
            if(!Guid.TryParse(context.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier),out var user)
                || await Accounts.DiscordIdentity(db,user,context.HttpContext.RequestAborted) is null)return Results.Unauthorized();
            return await next(context);
        });
        group.MapGet("",async (HttpContext http,PatreonConnections connections)=>Results.Ok(new {
            enabled=PatreonClient.Enabled(app.Configuration), connection=await connections.Read(User(http),http.RequestAborted) }));
        group.MapPost("/link",async (HttpContext http,[Microsoft.AspNetCore.Mvc.FromServices] NpgsqlDataSource db,IAntiforgery csrf)=>
        {
            if(!await Csrf(http,csrf))return Results.BadRequest();
            var origin=PatreonClient.Origin(app.Configuration);
            if(!PatreonClient.Enabled(app.Configuration))return Results.Json(new {error="patreon_not_configured"},statusCode:503);
            if(!http.Request.IsHttps || $"{http.Request.Scheme}://{http.Request.Host}"!=origin)return Results.BadRequest();
            var state=WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            await using var connection=await db.OpenConnectionAsync(http.RequestAborted);
            await using var tx=await connection.BeginTransactionAsync(http.RequestAborted);
            await PatreonConnections.Lock(connection,tx,User(http),http.RequestAborted);
            await using var command=new NpgsqlCommand("WITH cleanup AS (DELETE FROM gc.patreon_link_challenges WHERE user_id=$1 OR expires_at<now()) INSERT INTO gc.patreon_link_challenges(state_hash,user_id,expires_at) VALUES($2,$1,now()+interval '5 minutes')",connection,tx);
            command.Parameters.AddWithValue(User(http));command.Parameters.AddWithValue(Hash(state));await command.ExecuteNonQueryAsync(http.RequestAborted);await tx.CommitAsync(http.RequestAborted);
            http.Response.Cookies.Append(Cookie,state,new CookieOptions { Secure=true,HttpOnly=true,SameSite=SameSiteMode.Lax,Path="/",MaxAge=TimeSpan.FromMinutes(5) });
            return Results.Ok(new {url=QueryHelpers.AddQueryString("https://www.patreon.com/oauth2/authorize",new Dictionary<string,string?> {
                ["response_type"]="code",["client_id"]=app.Configuration["Patreon:ClientId"],
                ["redirect_uri"]=origin+"/auth/patreon/callback",["scope"]="identity identity.memberships",["state"]=state })});
        });
        group.MapDelete("",async (HttpContext http,PatreonConnections connections,IAntiforgery csrf)=>
        {
            if(!await Csrf(http,csrf))return Results.BadRequest();
            await connections.Disconnect(User(http),http.RequestAborted);return Results.NoContent();
        });
        app.MapGet("/auth/patreon/callback",async (HttpContext http,[Microsoft.AspNetCore.Mvc.FromServices] NpgsqlDataSource db,PatreonClient client,PatreonConnections connections)=>
        {
            http.Response.Headers.CacheControl="no-store";
            IResult Finish(string result)=>Results.Redirect("/account.html?patreon="+result+"#connections");
            var origin=PatreonClient.Origin(app.Configuration);
            var state=http.Request.Query["state"].ToString();
            if(!PatreonClient.Enabled(app.Configuration) || !http.Request.IsHttps || $"{http.Request.Scheme}://{http.Request.Host}"!=origin
                || state.Length!=43 || http.Request.Cookies[Cookie]!=state || http.Request.Query.Any(p=>p.Value.Count!=1 || p.Value.ToString().Length>2048))return Finish("failed");
            http.Response.Cookies.Delete(Cookie,new CookieOptions{Secure=true,HttpOnly=true,Path="/"});
            if(await Accounts.DiscordIdentity(db,User(http),http.RequestAborted) is null)return Finish("failed");
            await using var eligible=db.CreateCommand("SELECT user_id FROM gc.patreon_link_challenges WHERE state_hash=$1 AND user_id=$2 AND expires_at>now()");
            eligible.Parameters.AddWithValue(Hash(state));eligible.Parameters.AddWithValue(User(http));
            if(await eligible.ExecuteScalarAsync(http.RequestAborted) is not Guid)return Finish("failed");
            if(http.Request.Query.ContainsKey("error"))return Finish("cancelled");
            var code=http.Request.Query["code"].ToString();if(string.IsNullOrWhiteSpace(code))return Finish("failed");
            try
            {
                var tokens=await client.Exchange(code,false,http.RequestAborted);
                var membership=await client.Identity(tokens,http.RequestAborted);
                return Finish(await connections.Complete(User(http),Hash(state),tokens,membership,http.RequestAborted));
            }
            catch(Exception error) when(error is HttpRequestException or OperationCanceledException or JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException or FormatException)
            {return Finish("failed");}
        }).RequireAuthorization();
    }
    private static Guid User(HttpContext http)=>Guid.Parse(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    private static string Hash(string state)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state)));
    private static async Task<bool> Csrf(HttpContext http,IAntiforgery csrf) { try {await csrf.ValidateRequestAsync(http);return true;} catch(AntiforgeryValidationException){return false;} }
}
