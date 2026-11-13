using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace GC.Web;

// One campaign-wide snapshot, independent of which patrons have linked website accounts.
public static class DonationSync
{
    public sealed record Member(string Subject, decimal? MonthlyCents, DateTimeOffset? SupportingSince);
    public static bool Enabled(IConfiguration config) => PatreonClient.ValidId(config["Patreon:CampaignId"])
        && !string.IsNullOrWhiteSpace(config["Patreon:CreatorAccessToken"]);

    public static Member? ParseMember(JsonElement member)
    {
        if (member.GetProperty("type").GetString() != "member") throw new InvalidDataException("Invalid member resource.");
        var attributes = member.GetProperty("attributes");
        if (attributes.GetProperty("patron_status").GetString() != "active_patron"
            || attributes.GetProperty("last_charge_status").GetString() != "Paid"
            || attributes.GetProperty("currently_entitled_amount_cents").GetInt64() <= 0) return null;
        var user = member.GetProperty("relationships").GetProperty("user").GetProperty("data");
        var subject = user.GetProperty("id").GetString();
        if (user.GetProperty("type").GetString() != "user" || !PatreonClient.ValidId(subject))
            throw new InvalidDataException("Invalid patron identity.");
        // Upcoming pledge / months per charge, not a claim about cash collected or net payouts.
        decimal? monthly = null;
        if (attributes.TryGetProperty("will_pay_amount_cents", out var amount) && amount.ValueKind == JsonValueKind.Number && amount.TryGetInt64(out var cents)
            && cents >= 0 && attributes.TryGetProperty("pledge_cadence", out var cadence)
            && int.TryParse(cadence.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var months) && months > 0 && months <= 120)
            monthly = (decimal)cents / months;
        DateTimeOffset? since = null;
        if (attributes.TryGetProperty("pledge_relationship_start", out var start) && start.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(start.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            && date <= DateTimeOffset.UtcNow) since = date.ToUniversalTime();
        return new(subject!, monthly, since);
    }

    public static async Task Refresh(NpgsqlDataSource db, HttpClient client, IConfiguration config, CancellationToken token)
    {
        var campaign = config["Patreon:CampaignId"]!;
        var access = config["Patreon:CreatorAccessToken"]!;
        async Task<JsonDocument> Get(string path)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.patreon.com/api/oauth2/v2/" + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            using var response = await client.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
            return JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token));
        }
        using var campaignResponse = await Get($"campaigns/{campaign}?fields%5Bcampaign%5D=currency,is_monthly");
        var data = campaignResponse.RootElement.GetProperty("data");
        if (data.GetProperty("id").GetString() != campaign || data.GetProperty("type").GetString() != "campaign")
            throw new InvalidDataException("Campaign mismatch.");
        var attributes = data.GetProperty("attributes");
        if (attributes.GetProperty("currency").GetString() != "USD" || !attributes.GetProperty("is_monthly").GetBoolean())
            throw new InvalidDataException("Donation goal requires a monthly USD campaign.");
        var members = new Dictionary<string, Member>();
        var cursors = new HashSet<string>();
        string? cursor = null;
        do
        {
            var path = $"campaigns/{campaign}/members?include=user&fields%5Bmember%5D=patron_status,last_charge_status,currently_entitled_amount_cents,will_pay_amount_cents,pledge_cadence,pledge_relationship_start&page%5Bcount%5D=1000";
            if (cursor is not null) path += "&page%5Bcursor%5D=" + Uri.EscapeDataString(cursor);
            using var page = await Get(path);
            foreach (var item in page.RootElement.GetProperty("data").EnumerateArray())
            {
                var member = ParseMember(item);
                if (member is not null && !members.TryAdd(member.Subject, member)) throw new InvalidDataException("Duplicate patron.");
            }
            cursor = null;
            if (page.RootElement.TryGetProperty("meta", out var meta) && meta.TryGetProperty("pagination", out var pagination)
                && pagination.TryGetProperty("cursors", out var nextCursors) && nextCursors.TryGetProperty("next", out var next)
                && next.ValueKind == JsonValueKind.String) cursor = next.GetString();
            if (cursor is not null && (!cursors.Add(cursor) || cursors.Count > 100)) throw new InvalidDataException("Invalid pagination.");
        } while (cursor is not null);
        await Save(db, campaign, members.Values, token);
    }

    public static async Task Save(NpgsqlDataSource db, string campaign, IEnumerable<Member> members, CancellationToken token)
    {
        await using var connection = await db.OpenConnectionAsync(token);
        await using var tx = await connection.BeginTransactionAsync(token);
        await using (var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1,0))", connection, tx))
        { gate.Parameters.AddWithValue("donations:" + campaign); await gate.ExecuteNonQueryAsync(token); }
        await using (var clear = new NpgsqlCommand("DELETE FROM gc.patreon_campaign_members WHERE campaign_id=$1", connection, tx))
        { clear.Parameters.AddWithValue(campaign); await clear.ExecuteNonQueryAsync(token); }
        foreach (var member in members)
        {
            await using var insert = new NpgsqlCommand("INSERT INTO gc.patreon_campaign_members(campaign_id,subject,monthly_cents,supporting_since) VALUES($1,$2,$3,$4)", connection, tx);
            insert.Parameters.AddWithValue(campaign); insert.Parameters.AddWithValue(member.Subject);
            insert.Parameters.AddWithValue(NpgsqlDbType.Numeric, (object?)member.MonthlyCents ?? DBNull.Value);
            insert.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, (object?)member.SupportingSince ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync(token);
        }
        await using var sync = new NpgsqlCommand("INSERT INTO gc.patreon_campaign_sync(campaign_id,checked_at,currency) VALUES($1,now(),'USD') ON CONFLICT(campaign_id) DO UPDATE SET checked_at=excluded.checked_at,currency=excluded.currency", connection, tx);
        sync.Parameters.AddWithValue(campaign); await sync.ExecuteNonQueryAsync(token);
        await tx.CommitAsync(token);
    }
}

public sealed class DonationRefresh(IServiceProvider services, IConfiguration config, ILogger<DonationRefresh> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DonationSync.Enabled(config))
                {
                    using var scope = services.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
                    using var client = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>().CreateClient("patreon");
                    await DonationSync.Refresh(db, client, config, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { log.LogWarning("Donation sync unavailable; retaining the last complete snapshot."); }
            await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
        }
    }
}
