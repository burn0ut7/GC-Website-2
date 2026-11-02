using GC.Infrastructure;
using Npgsql;

namespace GC.Web;

public static class MissionRoutes
{
    public static void Map(RouteGroupBuilder group,Func<HttpContext,Guid> server)
    {
        group.AddEndpointFilter(async (context,next)=>
        {
            context.HttpContext.Response.Headers.CacheControl="no-store";
            if(context.HttpContext.RequestServices.GetService<NpgsqlDataSource>() is null)return Results.StatusCode(503);
            try{return await next(context);}
            catch(ArgumentException){return Results.BadRequest(new{error="invalid_query"});}
            catch(InvalidDataException){return Results.UnprocessableEntity(new{error="event_exceeds_read_limit",message="Inspect the private source archive; no events were skipped."});}
        });
        group.MapGet("",async(HttpContext context,Guid? after)=>Results.Ok(await Missions.List(
            context.RequestServices.GetRequiredService<NpgsqlDataSource>(),server(context),after,context.RequestAborted)));
        group.MapGet("/{mission:guid}",async(HttpContext context,Guid mission)=>
        {
            var summary=await Missions.Summary(context.RequestServices.GetRequiredService<NpgsqlDataSource>(),server(context),mission,context.RequestAborted);
            return summary is null?Results.NotFound():Results.Ok(summary);
        });
        group.MapGet("/{mission:guid}/players",async(HttpContext context,Guid mission,string? after)=>Results.Ok(await Missions.Players(
            context.RequestServices.GetRequiredService<NpgsqlDataSource>(),server(context),mission,after,context.RequestAborted)));
        group.MapGet("/{mission:guid}/events",async(HttpContext context,Guid mission,int? afterSequence,string? type)=>Results.Ok(await Missions.Events(
            context.RequestServices.GetRequiredService<NpgsqlDataSource>(),server(context),mission,afterSequence??0,type,context.RequestAborted)));
    }
}
