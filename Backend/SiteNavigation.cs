using Microsoft.AspNetCore.Authorization;
namespace GC.Web;

public static class SiteNavigation
{
    public const string AdminPolicy = "administrator";
    public sealed record Page(string Label, string Href, string? Policy = null, bool SignedIn = false);
    // Add future pages here; protected routes must enforce the same policy.
    public static readonly Page[] Pages = [new("Home", "/"), new("After Action Report", "/aar"), new("Your account", "/account.html", SignedIn: true), new("Administration", "/admin", AdminPolicy)];
    public static void Map(WebApplication app) => app.MapGet("/api/navigation", async Task<IResult> (HttpContext context, IAuthorizationService authorization) =>
    {
        context.Response.Headers.CacheControl = "no-store";
        var visible = new List<object>();
        foreach (var page in Pages)
        {
            if (page.SignedIn && context.User.Identity?.IsAuthenticated != true) continue;
            if (page.Policy is not null && !(await authorization.AuthorizeAsync(context.User, null, page.Policy)).Succeeded) continue;
            visible.Add(new { page.Label, page.Href });
        }
        return Results.Ok(visible);
    });
}
