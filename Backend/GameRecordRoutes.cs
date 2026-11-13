using System.Security.Claims;
using GC.Infrastructure;
using Npgsql;

namespace GC.Web;

public static class GameRecordRoutes
{
    public static void Map(WebApplication app,RouteGroupBuilder admin)
    {
        foreach(var page in new[]{"/sessions","/sessions/{server:guid}/{session:guid}","/players"})
            app.MapGet(page,()=>Results.File(Path.Combine(AppContext.BaseDirectory,"PrivatePages","game-record.html"),"text/html"))
                .WithMetadata(new Identity.ProtectedPage()).RequireAuthorization(SiteNavigation.AdminPolicy);

        var routes=admin.MapGroup("/game");
        routes.AddEndpointFilter(async (context,next)=>{
            context.HttpContext.Response.Headers.CacheControl="no-store";
            if(context.HttpContext.RequestServices.GetService<NpgsqlDataSource>() is null)return Results.StatusCode(503);
            return await next(context);
        });
        routes.MapGet("/sessions/{server:guid}/{session:guid}",async (Guid server,Guid session,HttpContext context,CancellationToken ct)=>
            await GameRecords.Session(context.RequestServices.GetRequiredService<NpgsqlDataSource>(),server,session,ct) is {} record?Results.Ok(record):Results.NotFound());
        routes.MapGet("/sessions",async(int? offset,string? search,string? sort,string? direction,bool? mine,HttpContext context,CancellationToken ct)=>
        {
            if (offset is <0 or >100000 || search?.Length>200 || sort is not (null or "time" or "mission" or "map" or "outcome" or "duration") || direction is not (null or "asc" or "desc")) return Results.BadRequest();
            Guid? user=null;
            if (mine == true)
            {
                if (!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier),out var id)) return Results.Unauthorized();
                user=id;
            }
            return Results.Ok(await MissionCatalog.Sessions(context.RequestServices.GetRequiredService<NpgsqlDataSource>(),null,null,offset??0,ct,search??"",sort??"time",direction=="asc",user,MapPackages.FromConfiguration(app.Configuration).AvailableMaps(app.Environment.WebRootPath).Select(map => new SessionMapIdentity((string)map!["name"]!, (string)map["worldSuffix"]!)).ToArray()));
        });
        routes.MapGet("/players",async(string? search,int? offset,HttpContext context,CancellationToken ct)=>
            search?.Length>200 || offset is <0 or >100000?Results.BadRequest():Results.Ok(await GameRecords.Players(context.RequestServices.GetRequiredService<NpgsqlDataSource>(),search??"",offset??0,ct)));
        routes.MapGet("/player",async(string identity,HttpContext context,CancellationToken ct)=>
            identity.Length is 0 or >256?Results.BadRequest():await GameRecords.Player(context.RequestServices.GetRequiredService<NpgsqlDataSource>(),identity,ct) is {} player?Results.Ok(player):Results.NotFound());
        routes.MapGet("/player/sessions",async(string identity,int? offset,HttpContext context,CancellationToken ct)=>
            identity.Length is 0 or >256 || offset is <0 or >100000?Results.BadRequest():Results.Ok(await GameRecords.PlayerSessions(context.RequestServices.GetRequiredService<NpgsqlDataSource>(),identity,offset??0,ct)));
    }
}
