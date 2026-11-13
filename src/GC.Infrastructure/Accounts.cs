using Npgsql;

namespace GC.Infrastructure;

public static class Accounts
{
    public static async Task<(string Subject, string Name)?> DiscordIdentity(NpgsqlDataSource database, Guid user, CancellationToken token)
    {
        await using var query = database.CreateCommand("SELECT subject,display_name FROM gc.external_identities WHERE provider='discord' AND user_id=$1");
        query.Parameters.AddWithValue(user);
        await using var reader = await query.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? (reader.GetString(0), reader.GetString(1)) : null;
    }

    public static async Task<Guid> SignInDiscord(NpgsqlDataSource database, string subject, string name, CancellationToken token, string? avatarHash = null)
    {
        if (subject.Length is < 1 or > 32 || !subject.All(char.IsAsciiDigit) || name.Length > 200)
            throw new ArgumentException("Invalid Discord identity.");
        if (avatarHash is not null && !System.Text.RegularExpressions.Regex.IsMatch(avatarHash, "^(a_)?[a-f0-9]{32}$"))
            throw new ArgumentException("Invalid Discord avatar.");
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using (var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1, 0))", connection, transaction))
        {
            gate.Parameters.AddWithValue("discord:" + subject);
            await gate.ExecuteNonQueryAsync(token);
        }
        await using var lookup = new NpgsqlCommand("SELECT user_id FROM gc.external_identities WHERE provider='discord' AND subject=$1", connection, transaction);
        lookup.Parameters.AddWithValue(subject);
        var existing = await lookup.ExecuteScalarAsync(token);
        var id = existing is Guid user ? user : Guid.NewGuid();
        if (existing is null)
        {
            await using var create = new NpgsqlCommand("INSERT INTO gc.users(id) VALUES ($1)", connection, transaction);
            create.Parameters.AddWithValue(id);
            await create.ExecuteNonQueryAsync(token);
        }
        await using var identity = new NpgsqlCommand("""
            INSERT INTO gc.external_identities(provider, subject, user_id, display_name, avatar_hash) VALUES ('discord', $1, $2, $3, $4)
            ON CONFLICT (provider, subject) DO UPDATE SET display_name=excluded.display_name, avatar_hash=excluded.avatar_hash
            """, connection, transaction);
        identity.Parameters.AddWithValue(subject); identity.Parameters.AddWithValue(id); identity.Parameters.AddWithValue(name);
        identity.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Text, (object?)avatarHash ?? DBNull.Value);
        await identity.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
        return id;
    }
}
