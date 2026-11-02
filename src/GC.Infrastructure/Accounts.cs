using Npgsql;

namespace GC.Infrastructure;

public static class Accounts
{
    public static async Task<Guid> SignInDiscord(NpgsqlDataSource database, string subject, string name, CancellationToken token)
    {
        if (subject.Length is < 1 or > 32 || !subject.All(char.IsAsciiDigit) || name.Length > 200)
            throw new ArgumentException("Invalid Discord identity.");
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
            INSERT INTO gc.external_identities(provider, subject, user_id, display_name) VALUES ('discord', $1, $2, $3)
            ON CONFLICT (provider, subject) DO UPDATE SET display_name=excluded.display_name
            """, connection, transaction);
        identity.Parameters.AddWithValue(subject); identity.Parameters.AddWithValue(id); identity.Parameters.AddWithValue(name);
        await identity.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
        return id;
    }
}
