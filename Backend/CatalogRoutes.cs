using GC.Infrastructure;
using Npgsql;
namespace GC.Web;

public static class CatalogRoutes
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/aar", () => Results.File(Path.Combine(AppContext.BaseDirectory,"PrivatePages","aar.html"),"text/html"));
        app.MapGet("/missions", () => Results.File(Path.Combine(app.Environment.WebRootPath,"missions.html"),"text/html"));
        MapRead(app.MapGroup("/api/catalog"));
        MapSessions(app.MapGroup("/api/admin/catalog").RequireAuthorization(SiteNavigation.AdminPolicy), _=>null);
    }
    public static void MapRead(RouteGroupBuilder group)
    {
        Guard(group);
        group.MapGet("",async(HttpContext context,string? search,string? tag,int? offset,CancellationToken ct)=>Results.Ok(await MissionCatalog.List(context.RequestServices.GetRequiredService<NpgsqlDataSource>(),search,tag,offset??0,ct)));
        group.MapGet("/status",async(HttpContext context,CancellationToken ct)=>Results.Ok(await MissionCatalog.Status(context.RequestServices.GetRequiredService<NpgsqlDataSource>(),ct)));
        group.MapGet("/{id:guid}",async(HttpContext context,Guid id,CancellationToken ct)=>await MissionCatalog.Detail(context.RequestServices.GetRequiredService<NpgsqlDataSource>(),id,ct) is {} item?Results.Ok(item):Results.NotFound());
    }
    public static void MapSessions(RouteGroupBuilder group,Func<HttpContext,Guid?> server)
    {
        Guard(group);
        group.MapGet("/{id:guid}/statistics",async(HttpContext context,Guid id,CancellationToken ct)=>Results.Ok(await MissionCatalog.Statistics(context.RequestServices.GetRequiredService<NpgsqlDataSource>(),id,server(context),ct)));
        group.MapGet("/{id:guid}/sessions",async(HttpContext context,Guid id,int? offset,CancellationToken ct)=>Results.Ok(await MissionCatalog.Sessions(context.RequestServices.GetRequiredService<NpgsqlDataSource>(),id,server(context),offset??0,ct)));
    }
    private static void Guard(RouteGroupBuilder group)=>group.AddEndpointFilter(async(context,next)=>{
        context.HttpContext.Response.Headers.CacheControl="no-store";
        if(context.HttpContext.RequestServices.GetService<NpgsqlDataSource>() is null)return Results.StatusCode(503);
        try{return await next(context);}catch(ArgumentException){return Results.BadRequest(new{error="invalid_filter"});}
    });
}
