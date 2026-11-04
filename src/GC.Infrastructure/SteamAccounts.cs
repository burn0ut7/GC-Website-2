using System.Globalization;
using Npgsql;

namespace GC.Infrastructure;

public static class SteamAccounts
{
    // Public-universe individual desktop accounts, represented as strings throughout JS/JSON.
    public static bool ValidId(string? id) => id is { Length: 17 } && id.All(char.IsAsciiDigit)
        && ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
        && (value >> 56) == 1 && ((value >> 52) & 15) == 1 && ((value >> 32) & 0xfffff) == 1
        && (value & 0xffffffff) != 0;

    public static async Task<bool> Link(NpgsqlDataSource db, Guid user, string steam, CancellationToken token)
    {
        await using var connection=await db.OpenConnectionAsync(token);
        await using var tx=await connection.BeginTransactionAsync(token);
        await Lock(connection,tx,user,token);
        var result=await Link(connection,tx,user,steam,token);
        await tx.CommitAsync(token);
        return result;
    }
    private static async Task<bool> Link(NpgsqlConnection connection,NpgsqlTransaction tx, Guid user, string steam, CancellationToken token)
    {
        if (!ValidId(steam)) throw new ArgumentException("Invalid Steam ID.");
        await using var command = new NpgsqlCommand("""
            INSERT INTO gc.external_identities(provider,subject,user_id,display_name)
            VALUES('steam',$1,$2,$1) ON CONFLICT DO NOTHING
            """,connection,tx);
        command.Parameters.AddWithValue(steam); command.Parameters.AddWithValue(user);
        if (await command.ExecuteNonQueryAsync(token) == 1) return true;
        await using var check = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM gc.external_identities WHERE provider='steam' AND subject=$1 AND user_id=$2)",connection,tx);
        check.Parameters.AddWithValue(steam); check.Parameters.AddWithValue(user);
        return (bool)(await check.ExecuteScalarAsync(token))!;
    }
    private static async Task Lock(NpgsqlConnection connection,NpgsqlTransaction tx,Guid user,CancellationToken token)
    {
        await using var gate=new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1,0))",connection,tx);
        gate.Parameters.AddWithValue("steam-link:"+user);await gate.ExecuteNonQueryAsync(token);
    }
    public static async Task<string> Complete(NpgsqlDataSource db,Guid user,string stateHash,string steam,CancellationToken token)
    {
        await using var connection=await db.OpenConnectionAsync(token);
        await using var tx=await connection.BeginTransactionAsync(token);
        await Lock(connection,tx,user,token);
        await using var consume=new NpgsqlCommand("DELETE FROM gc.steam_link_challenges WHERE state_hash=$1 AND user_id=$2 AND expires_at>now() RETURNING user_id",connection,tx);
        consume.Parameters.AddWithValue(stateHash);consume.Parameters.AddWithValue(user);
        if(await consume.ExecuteScalarAsync(token) is not Guid)return "failed";
        var result=await Link(connection,tx,user,steam,token)?"linked":"conflict";
        await tx.CommitAsync(token);
        return result;
    }
    public static async Task Disconnect(NpgsqlDataSource db,Guid user,CancellationToken token)
    {
        await using var connection=await db.OpenConnectionAsync(token);
        await using var tx=await connection.BeginTransactionAsync(token);
        await Lock(connection,tx,user,token);
        await using var command=new NpgsqlCommand("WITH cleanup AS (DELETE FROM gc.steam_link_challenges WHERE user_id=$1) DELETE FROM gc.external_identities WHERE provider='steam' AND user_id=$1",connection,tx);
        command.Parameters.AddWithValue(user);await command.ExecuteNonQueryAsync(token);
        await tx.CommitAsync(token);
    }

    public static async Task Observe(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid server, Guid mission,
        string identity, string steam, CancellationToken token)
    {
        if (!ValidId(steam) || !Guid.TryParse(identity, out var uid) || uid == Guid.Empty)
            throw new InvalidDataException("Invalid server Steam identity observation.");
        await using var command = new NpgsqlCommand("""
            INSERT INTO gc.game_identity_observations(identity,steam_id,server_id,mission_id) VALUES($1,$2,$3,$4)
            ON CONFLICT(identity,steam_id,server_id) DO UPDATE SET last_seen_at=now(),mission_id=excluded.mission_id
            """, connection, transaction);
        command.Parameters.AddWithValue(uid); command.Parameters.AddWithValue(steam);
        command.Parameters.AddWithValue(server); command.Parameters.AddWithValue(mission);
        await command.ExecuteNonQueryAsync(token);
    }

    public sealed record GameAccount(Guid UserId, string DiscordId, string SteamId, Guid ArmaIdentity);
    public static async Task<GameAccount?> Resolve(NpgsqlDataSource db, Guid identity, CancellationToken token)
    {
        await using var query=db.CreateCommand("""
            SELECT s.user_id,d.subject,s.subject FROM gc.verified_game_identities g
            JOIN gc.external_identities s ON s.provider='steam' AND s.subject=g.steam_id
            JOIN gc.external_identities d ON d.provider='discord' AND d.user_id=s.user_id
            WHERE g.identity=$1
            """);
        query.Parameters.AddWithValue(identity);
        await using var reader=await query.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token)?new(reader.GetGuid(0),reader.GetString(1),reader.GetString(2),identity):null;
    }

    public static async Task<object> Read(NpgsqlDataSource db, Guid user, CancellationToken token)
    {
        await using var command = db.CreateCommand("""
            SELECT e.subject,v.identity,
                EXISTS(SELECT 1 FROM gc.game_identity_observations o WHERE o.steam_id=e.subject)
            FROM gc.external_identities e LEFT JOIN gc.verified_game_identities v ON v.steam_id=e.subject
            WHERE e.user_id=$1 AND e.provider='steam'
            """);
        command.Parameters.AddWithValue(user);
        string? steam=null; Guid? identity=null; bool observed=false;
        await using (var reader=await command.ExecuteReaderAsync(token))
            if(await reader.ReadAsync(token)) { steam=reader.GetString(0);identity=reader.IsDBNull(1)?null:reader.GetGuid(1);observed=reader.GetBoolean(2); }
        return new { steamId=steam, armaIdentity=identity, status=steam is null?"not_linked":identity.HasValue?"matched":observed?"conflict":"awaiting_server" };
    }
}
