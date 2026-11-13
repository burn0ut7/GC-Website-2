using System.Security.Claims;
using System.Text.Json;
using GC.Infrastructure;
using Microsoft.AspNetCore.Antiforgery;
using Npgsql;

namespace GC.Web;

public static class DonationRoutes
{
    public sealed record Privacy(bool Anonymous = false, bool ShowAmount = false);
    public sealed record Donor(string Name, string? AvatarUrl, decimal? MonthlyCents, DateTimeOffset? SupportingSince);
    public sealed record Cost(string Name, decimal MonthlyCents, bool Estimated);
    public sealed record Snapshot(string Status, DateTimeOffset? CheckedAt, decimal? MonthlyCents, int SupporterCount,
        int AnonymousCount, IReadOnlyList<Donor> Donors);

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/donations", async (HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var costs = app.Configuration.GetSection("Donations:Costs").Get<Cost[]>() ?? [new("Game server", 10000, true)];
            if (costs.Length == 0 || costs.Any(c => string.IsNullOrWhiteSpace(c.Name) || c.MonthlyCents < 0) || costs.Sum(c => c.MonthlyCents) <= 0)
                return Results.Json(new { error = "invalid_cost_configuration" }, statusCode: 503);
            var db = http.RequestServices.GetService<NpgsqlDataSource>();
            var snapshot = new Snapshot("not_configured", null, null, 0, 0, []);
            if (db is not null && DonationSync.Enabled(app.Configuration))
            {
                try { snapshot = await Read(db, app.Configuration["Patreon:CampaignId"]!, http.RequestAborted); }
                catch (NpgsqlException) { snapshot = new("unavailable", null, null, 0, 0, []); }
            }
            return Results.Ok(new { currency = "USD", goalCents = costs.Sum(c => c.MonthlyCents), costs,
                patreonUrl = "https://www.patreon.com/c/globalconflicts/", funding = snapshot });
        });
        var group = app.MapGroup("/api/account/donations").RequireAuthorization();
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            http.Response.Headers.CacheControl = "no-store";
            var db = http.RequestServices.GetService<NpgsqlDataSource>();
            if (db is null) return Results.StatusCode(503);
            if (!Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var user)
                || await Accounts.DiscordIdentity(db, user, http.RequestAborted) is null) return Results.Unauthorized();
            return await next(context);
        });
        group.MapGet("", async Task<IResult> (HttpContext http) => Results.Ok(await ReadPrivacy(
            http.RequestServices.GetRequiredService<NpgsqlDataSource>(), User(http), http.RequestAborted)));
        group.MapPut("", async (HttpContext http, IAntiforgery csrf) =>
        {
            try { await csrf.ValidateRequestAsync(http); }
            catch (AntiforgeryValidationException) { return Results.BadRequest(new { error = "csrf_required" }); }
            if (!http.Request.HasJsonContentType()) return Results.StatusCode(415);
            using var body = new MemoryStream();
            var buffer = new byte[1024];
            int count;
            while ((count = await http.Request.Body.ReadAsync(buffer, http.RequestAborted)) > 0)
            {
                if (body.Length + count > 1024) return Results.StatusCode(413);
                body.Write(buffer, 0, count);
            }
            Privacy? value;
            try { value = JsonSerializer.Deserialize<Privacy>(body.ToArray(), new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
            catch (JsonException) { return Results.BadRequest(); }
            if (value is null) return Results.BadRequest();
            // Full anonymity always wins over amount disclosure.
            value = value with { ShowAmount = !value.Anonymous && value.ShowAmount };
            await SavePrivacy(http.RequestServices.GetRequiredService<NpgsqlDataSource>(), User(http), value, http.RequestAborted);
            return Results.Ok(value);
        });
    }

    private static Guid User(HttpContext http) => Guid.Parse(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    public static async Task<Privacy> ReadPrivacy(NpgsqlDataSource db, Guid user, CancellationToken token)
    {
        await using var command = db.CreateCommand("SELECT anonymous,show_amount FROM gc.donation_preferences WHERE user_id=$1");
        command.Parameters.AddWithValue(user);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? new(reader.GetBoolean(0), reader.GetBoolean(1)) : new();
    }
    public static async Task SavePrivacy(NpgsqlDataSource db, Guid user, Privacy privacy, CancellationToken token)
    {
        await using var command = db.CreateCommand("INSERT INTO gc.donation_preferences(user_id,anonymous,show_amount) VALUES($1,$2,$3) ON CONFLICT(user_id) DO UPDATE SET anonymous=excluded.anonymous,show_amount=excluded.show_amount");
        command.Parameters.AddWithValue(user); command.Parameters.AddWithValue(privacy.Anonymous);
        command.Parameters.AddWithValue(!privacy.Anonymous && privacy.ShowAmount);
        await command.ExecuteNonQueryAsync(token);
    }
    public static async Task<Snapshot> Read(NpgsqlDataSource db, string campaign, CancellationToken token)
    {
        // A single statement reads one consistent snapshot, including current privacy settings.
        await using var command = db.CreateCommand("""
            SELECT s.checked_at,m.subject,m.monthly_cents,m.supporting_since,
                COALESCE(NULLIF(p.display_name,''),d.display_name),d.subject,d.avatar_hash,
                COALESCE(v.anonymous,false),COALESCE(v.show_amount,false)
            FROM gc.patreon_campaign_sync s
            LEFT JOIN gc.patreon_campaign_members m ON m.campaign_id=s.campaign_id
            LEFT JOIN gc.external_identities e ON e.provider='patreon' AND e.subject=m.subject
            LEFT JOIN gc.external_identities d ON d.provider='discord' AND d.user_id=e.user_id
            LEFT JOIN gc.account_preferences p ON p.user_id=e.user_id
            LEFT JOIN gc.donation_preferences v ON v.user_id=e.user_id
            WHERE s.campaign_id=$1
            """);
        command.Parameters.AddWithValue(campaign);
        await using var reader = await command.ExecuteReaderAsync(token);
        DateTimeOffset? checkedAt = null;
        decimal total = 0;
        var complete = true;
        var count = 0;
        var anonymous = 0;
        var donors = new List<Donor>();
        while (await reader.ReadAsync(token))
        {
            checkedAt = reader.GetFieldValue<DateTimeOffset>(0);
            if (reader.IsDBNull(1)) continue;
            count++;
            decimal? amount = reader.IsDBNull(2) ? null : reader.GetDecimal(2);
            if (amount.HasValue) total += amount.Value; else complete = false;
            if (reader.IsDBNull(4) || reader.GetBoolean(7)) { anonymous++; continue; }
            string? avatar = reader.IsDBNull(6) ? null : $"https://cdn.discordapp.com/avatars/{reader.GetString(5)}/{reader.GetString(6)}.png?size=96";
            donors.Add(new(reader.GetString(4), avatar, reader.GetBoolean(8) ? amount : null,
                reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3)));
        }
        if (checkedAt is null) return new("awaiting_sync", null, null, 0, 0, []);
        var status = checkedAt < DateTimeOffset.UtcNow.AddHours(-1) ? "stale" : complete ? "ready" : "incomplete";
        return new(status, checkedAt, complete ? total : null, count, anonymous,
            donors.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToArray());
    }
}
