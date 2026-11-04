using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;

namespace GC.Web;

public sealed class PatreonClient(IHttpClientFactory clients, IConfiguration config)
{
    public sealed record Tokens(string Access, string Refresh, DateTimeOffset Expires);
    public sealed record Membership(string Subject, string Status);
    public static string? Origin(IConfiguration config)
    {
        return Uri.TryCreate(config["Patreon:PublicOrigin"], UriKind.Absolute, out var uri)
            && uri.Scheme == "https" && uri.AbsolutePath == "/" && uri.Query == "" && uri.Fragment == "" && uri.UserInfo == ""
            ? uri.GetLeftPart(UriPartial.Authority) : null;
    }
    public static bool Enabled(IConfiguration config) => Origin(config) is not null
        && !string.IsNullOrWhiteSpace(config["Patreon:ClientId"]) && !string.IsNullOrWhiteSpace(config["Patreon:ClientSecret"])
        && ValidId(config["Patreon:CampaignId"]);
    public static bool ValidId(string? value) => value is { Length: > 0 and <= 30 } && value.All(char.IsAsciiDigit);
    public async Task<Tokens> Exchange(string value, bool refresh, CancellationToken token)
    {
        var fields = new Dictionary<string,string> {
            ["grant_type"] = refresh ? "refresh_token" : "authorization_code",
            [refresh ? "refresh_token" : "code"] = value,
            ["client_id"] = config["Patreon:ClientId"]!, ["client_secret"] = config["Patreon:ClientSecret"]!
        };
        if (!refresh) fields["redirect_uri"] = Origin(config) + "/auth/patreon/callback";
        using var client = clients.CreateClient("patreon");
        using var response = await client.PostAsync("https://www.patreon.com/api/oauth2/token", new FormUrlEncodedContent(fields), token);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token));
        var root = document.RootElement;
        var access = root.GetProperty("access_token").GetString();
        var nextRefresh = root.GetProperty("refresh_token").GetString();
        if (string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(nextRefresh)) throw new InvalidDataException("Missing Patreon token.");
        return new(access, nextRefresh, DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(root.GetProperty("expires_in").GetInt64(), 0, 31536000)));
    }
    public async Task<Membership> Identity(Tokens tokens, CancellationToken token)
    {
        using var client = clients.CreateClient("patreon");
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://www.patreon.com/api/oauth2/v2/identity?include=memberships.campaign&fields%5Bmember%5D=patron_status,currently_entitled_amount_cents,last_charge_status");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.Access);
        using var response = await client.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token));
        return Parse(document.RootElement, config["Patreon:CampaignId"]!);
    }
    public static Membership Parse(JsonElement root, string campaign)
    {
        var user = root.GetProperty("data");
        var subject = user.GetProperty("id").GetString();
        if (user.GetProperty("type").GetString() != "user" || !ValidId(subject)) throw new InvalidDataException("Invalid Patreon identity.");
        // Only inspect membership resources explicitly related to this authenticated user.
        var memberships = user.GetProperty("relationships").GetProperty("memberships").GetProperty("data");
        var ids = memberships.EnumerateArray().Where(x => x.GetProperty("type").GetString() == "member")
            .Select(x => x.GetProperty("id").GetString()).ToHashSet();
        if (ids.Count == 0) return new(subject!, "not_supporting");
        var included = root.GetProperty("included").EnumerateArray().Where(x => x.GetProperty("type").GetString() == "member" && ids.Contains(x.GetProperty("id").GetString())).ToArray();
        if (included.Length != ids.Count) throw new InvalidDataException("Incomplete Patreon memberships.");
        foreach (var member in included)
        {
            var relation = member.GetProperty("relationships").GetProperty("campaign").GetProperty("data");
            if (relation.GetProperty("type").GetString() != "campaign") throw new InvalidDataException("Invalid campaign relationship.");
            if (relation.GetProperty("id").GetString() != campaign) continue;
            var attributes = member.GetProperty("attributes");
            var status = attributes.GetProperty("patron_status").GetString();
            var amount = attributes.GetProperty("currently_entitled_amount_cents");
            var charge = attributes.GetProperty("last_charge_status").GetString();
            if (status == "declined_patron" || charge == "Declined") return new(subject!, "payment_issue");
            if (status == "active_patron" && amount.ValueKind == JsonValueKind.Number && amount.GetInt64() > 0 && charge == "Paid") return new(subject!, "supporting");
            if (status is null or "former_patron" || (amount.ValueKind == JsonValueKind.Number && amount.GetInt64() == 0)) return new(subject!, "not_supporting");
            return new(subject!, "unverified");
        }
        return new(subject!, "not_supporting");
    }
}

public sealed class PatreonConnections(NpgsqlDataSource db, IDataProtectionProvider protection, PatreonClient client, IConfiguration config)
{
    private readonly IDataProtector protector = protection.CreateProtector("GC.PatreonTokens.v1");
    public sealed record Status(bool Connected, string SupportStatus, DateTimeOffset? CheckedAt);
    public static async Task Lock(NpgsqlConnection connection, NpgsqlTransaction tx, Guid user, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1,0))", connection, tx);
        command.Parameters.AddWithValue("patreon:" + user); await command.ExecuteNonQueryAsync(token);
    }
    public async Task<string> Complete(Guid user, string hash, PatreonClient.Tokens tokens, PatreonClient.Membership membership, CancellationToken token)
    {
        await using var connection = await db.OpenConnectionAsync(token);
        await using var tx = await connection.BeginTransactionAsync(token);
        await Lock(connection, tx, user, token);
        await using var consume = new NpgsqlCommand("DELETE FROM gc.patreon_link_challenges WHERE user_id=$1 AND state_hash=$2 AND expires_at>now() RETURNING user_id", connection, tx);
        consume.Parameters.AddWithValue(user); consume.Parameters.AddWithValue(hash);
        if (await consume.ExecuteScalarAsync(token) is not Guid) return "failed";
        await using var insert = new NpgsqlCommand("INSERT INTO gc.external_identities(provider,subject,user_id,display_name) VALUES('patreon',$1,$2,$1) ON CONFLICT DO NOTHING", connection, tx);
        insert.Parameters.AddWithValue(membership.Subject); insert.Parameters.AddWithValue(user); await insert.ExecuteNonQueryAsync(token);
        await using var check = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM gc.external_identities WHERE provider='patreon' AND subject=$1 AND user_id=$2)", connection, tx);
        check.Parameters.AddWithValue(membership.Subject); check.Parameters.AddWithValue(user);
        if (!(bool)(await check.ExecuteScalarAsync(token))!) { await tx.CommitAsync(token); return "conflict"; }
        await using var save = new NpgsqlCommand("""
            INSERT INTO gc.patreon_connections(user_id,subject,protected_tokens,campaign_id,support_status) VALUES($1,$2,$3,$4,$5)
            ON CONFLICT(user_id) DO UPDATE SET protected_tokens=excluded.protected_tokens,campaign_id=excluded.campaign_id,
                support_status=excluded.support_status,checked_at=now(),next_check_at=now()+interval '15 minutes'
            """, connection, tx);
        save.Parameters.AddWithValue(user); save.Parameters.AddWithValue(membership.Subject);
        save.Parameters.AddWithValue(protector.Protect(JsonSerializer.Serialize(tokens)));
        save.Parameters.AddWithValue(config["Patreon:CampaignId"]!); save.Parameters.AddWithValue(membership.Status);
        await save.ExecuteNonQueryAsync(token); await tx.CommitAsync(token); return "linked";
    }
    public async Task Disconnect(Guid user, CancellationToken token)
    {
        await using var connection = await db.OpenConnectionAsync(token); await using var tx = await connection.BeginTransactionAsync(token);
        await Lock(connection, tx, user, token);
        await using var command = new NpgsqlCommand("WITH challenges AS (DELETE FROM gc.patreon_link_challenges WHERE user_id=$1), connections AS (DELETE FROM gc.patreon_connections WHERE user_id=$1) DELETE FROM gc.external_identities WHERE provider='patreon' AND user_id=$1", connection, tx);
        command.Parameters.AddWithValue(user); await command.ExecuteNonQueryAsync(token); await tx.CommitAsync(token);
    }
    public async Task<Status> Read(Guid user, CancellationToken token)
    {
        await using var command = db.CreateCommand("SELECT support_status,checked_at,campaign_id FROM gc.patreon_connections WHERE user_id=$1");
        command.Parameters.AddWithValue(user); await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return new(false, "not_linked", null);
        var checkedAt = reader.GetFieldValue<DateTimeOffset>(1);
        return new(true, !PatreonClient.Enabled(config) || reader.GetString(2) != config["Patreon:CampaignId"] || checkedAt < DateTimeOffset.UtcNow.AddMinutes(-30) ? "unverified" : reader.GetString(0), checkedAt);
    }
    public async Task Refresh(Guid user, CancellationToken token)
    {
        if (!PatreonClient.Enabled(config)) return;
        await using var connection = await db.OpenConnectionAsync(token); await using var tx = await connection.BeginTransactionAsync(token);
        await Lock(connection, tx, user, token);
        string subject, encrypted;
        await using (var select = new NpgsqlCommand("SELECT subject,protected_tokens FROM gc.patreon_connections WHERE user_id=$1 AND next_check_at<=now()", connection, tx))
        {
            select.Parameters.AddWithValue(user); await using var reader = await select.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) return;
            subject=reader.GetString(0); encrypted=reader.GetString(1);
        }
        string status;
        try
        {
            var tokens = JsonSerializer.Deserialize<PatreonClient.Tokens>(protector.Unprotect(encrypted))!;
            if (tokens.Expires < DateTimeOffset.UtcNow.AddMinutes(2))
            {
                tokens=await client.Exchange(tokens.Refresh, true, token);
                encrypted=protector.Protect(JsonSerializer.Serialize(tokens));
            }
            var membership=await client.Identity(tokens,token);
            if(membership.Subject!=subject)throw new InvalidDataException("Patreon identity changed.");
            status=membership.Status;
        }
        catch(Exception error) when(error is HttpRequestException or OperationCanceledException or JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        { token.ThrowIfCancellationRequested(); status="unverified"; }
        await using var update=new NpgsqlCommand("UPDATE gc.patreon_connections SET protected_tokens=$2,campaign_id=$3,support_status=$4,checked_at=now(),next_check_at=now()+interval '15 minutes' WHERE user_id=$1",connection,tx);
        update.Parameters.AddWithValue(user);update.Parameters.AddWithValue(encrypted);update.Parameters.AddWithValue(config["Patreon:CampaignId"]!);update.Parameters.AddWithValue(status);
        await update.ExecuteNonQueryAsync(token);await tx.CommitAsync(token);
    }
}

public sealed class PatreonRefresh(IServiceProvider services, IConfiguration config, ILogger<PatreonRefresh> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while(!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if(PatreonClient.Enabled(config))
                {
                    using var scope=services.CreateScope();
                    var db=scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
                    var connector=scope.ServiceProvider.GetRequiredService<PatreonConnections>();
                    var users=new List<Guid>();
                    await using(var command=db.CreateCommand("SELECT user_id FROM gc.patreon_connections WHERE next_check_at<=now() ORDER BY next_check_at LIMIT 100"))
                    await using(var reader=await command.ExecuteReaderAsync(stoppingToken))
                        while(await reader.ReadAsync(stoppingToken))users.Add(reader.GetGuid(0));
                    foreach(var user in users) { await connector.Refresh(user,stoppingToken);await Task.Delay(TimeSpan.FromSeconds(1),stoppingToken); }
                }
            }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){break;}
            catch(Exception){log.LogWarning("Patreon refresh unavailable; retrying next cycle.");}
            await Task.Delay(TimeSpan.FromMinutes(1),stoppingToken);
        }
    }
}
