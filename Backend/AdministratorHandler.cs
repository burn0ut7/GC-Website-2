using System.Security.Claims;
using GC.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Npgsql;

namespace GC.Web;

public sealed class AdministratorRequirement : IAuthorizationRequirement;

// Resolve authority on every request; no privileged role is cached in the login cookie.
public sealed class AdministratorHandler(IServiceProvider services, DiscordDeveloperAccess access)
    : AuthorizationHandler<AdministratorRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, AdministratorRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true ||
            !Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var user)) return;
        var database = services.GetService<NpgsqlDataSource>();
        if (database is null) return;
        var cancellation = (context.Resource as HttpContext)?.RequestAborted ?? CancellationToken.None;
        var identity = await Accounts.DiscordIdentity(database, user, cancellation);
        if (identity is { } discord && await access.HasRole(discord.Subject, cancellation))
            context.Succeed(requirement);
    }
}
