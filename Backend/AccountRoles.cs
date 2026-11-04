using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using GC.Infrastructure;
using Npgsql;

namespace GC.Web;

// A display snapshot only. Protected routes still enforce AdministratorHandler.
public sealed class AccountRoles(IConfiguration configuration, IHttpClientFactory clients)
{
    public sealed record Role(string Id, string Name, int Color, string[] Access);
    public sealed record Snapshot(string Status, Role[] Roles);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, (Snapshot Value, DateTimeOffset Until)> cache = new();
    private DateTimeOffset retryAfter;

    public async Task<Snapshot> Read(string subject, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (cache.TryGetValue(subject, out var cached) && cached.Until > now) return cached.Value;
            var guild = configuration["Discord:GuildId"];
            var credential = configuration["Discord:BotToken"];
            if (string.IsNullOrWhiteSpace(guild) || string.IsNullOrWhiteSpace(credential)) return new("unavailable", []);
            if (retryAfter > now) return new("unavailable", []);
            Snapshot result;
            try
            {
                using var member = await Fetch($"guilds/{guild}/members/{subject}", credential, token);
                if (member is null) result = new("not_member", []);
                else
                {
                    if (member.RootElement.GetProperty("user").GetProperty("id").GetString() != subject)
                        throw new InvalidDataException("Unexpected member identity.");
                    var ids = member.RootElement.GetProperty("roles").EnumerateArray().Select(x => x.GetString()).ToHashSet();
                    using var catalog = await Fetch($"guilds/{guild}/roles", credential, token)
                        ?? throw new InvalidDataException("Role catalog unavailable.");
                    var access = SiteNavigation.Pages.Where(p => p.Policy == SiteNavigation.AdminPolicy).Select(p => p.Label).ToArray();
                    var roles = catalog.RootElement.EnumerateArray().Where(r => ids.Contains(r.GetProperty("id").GetString()))
                        .OrderByDescending(r => r.GetProperty("position").GetInt32())
                        .Select(r => new Role(r.GetProperty("id").GetString()!, r.GetProperty("name").GetString()!,
                            r.GetProperty("color").GetInt32(), r.GetProperty("id").GetString() == configuration["Discord:DeveloperRoleId"] ? access : []))
                        .ToArray();
                    result = new("ok", roles);
                }
            }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException
                or InvalidOperationException or KeyNotFoundException or IOException or InvalidDataException or FormatException)
            {
                token.ThrowIfCancellationRequested();
                result = new("unavailable", []);
            }
            if (cache.Count >= 1024) cache.Clear();
            cache[subject] = (result, now.AddSeconds(result.Status == "ok" ? 30 : 5));
            return result;
        }
        finally { gate.Release(); }
    }

    private async Task<JsonDocument?> Fetch(string path, string credential, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://discord.com/api/v10/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bot", credential);
        using var client = clients.CreateClient("discord-roles");
        client.MaxResponseContentBufferSize = 512 * 1024;
        using var response = await client.SendAsync(request, token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var delay = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(30);
            retryAfter = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(delay.TotalSeconds, 1, 3600));
        }
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token));
    }

    public static void Map(WebApplication app) => app.MapGet("/api/account/roles", async Task<IResult>
        (HttpContext context, AccountRoles roles) =>
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)) return Results.Unauthorized();
        var database = context.RequestServices.GetService<NpgsqlDataSource>();
        if (database is null) return Results.StatusCode(503);
        var discord = await Accounts.DiscordIdentity(database, id, context.RequestAborted);
        if (discord is null) return Results.Unauthorized();
        return Results.Ok(await roles.Read(discord.Value.Subject, context.RequestAborted));
    }).RequireAuthorization();
}
