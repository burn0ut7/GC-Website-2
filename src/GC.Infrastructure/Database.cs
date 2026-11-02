using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace GC.Infrastructure;

public static class Database
{
    public static NpgsqlDataSource Open(string connectionString)
    {
        var settings = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Timeout = 5,
            CommandTimeout = 15,
            IncludeErrorDetail = false,
            ApplicationName = "GC"
        };
        return NpgsqlDataSource.Create(settings.ConnectionString);
    }

    public static async Task<bool> IsReady(NpgsqlDataSource source, CancellationToken token)
    {
        await using var command = source.CreateCommand("SELECT version, checksum FROM gc.schema_migrations ORDER BY version");
        await using var reader = await command.ExecuteReaderAsync(token);
        var applied = new Dictionary<string, string>();
        while (await reader.ReadAsync(token)) applied.Add(reader.GetString(0), reader.GetString(1));
        var migrations = ReadMigrations();
        return applied.Count == migrations.Count && migrations.All(m => applied.GetValueOrDefault(m.Name) == m.Hash);
    }

    public static async Task Migrate(NpgsqlDataSource source, CancellationToken token = default)
    {
        await using var connection = await source.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using (var command = new NpgsqlCommand("""
            SELECT pg_advisory_xact_lock(714824120);
            CREATE SCHEMA IF NOT EXISTS gc;
            CREATE TABLE IF NOT EXISTS gc.schema_migrations (
                version text PRIMARY KEY, checksum text NOT NULL, applied_at timestamptz NOT NULL DEFAULT now()
            );
            """, connection, transaction)) await command.ExecuteNonQueryAsync(token);
        var migrations = ReadMigrations();
        var applied = new Dictionary<string, string>();
        await using (var command = new NpgsqlCommand("SELECT version, checksum FROM gc.schema_migrations", connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token)) applied.Add(reader.GetString(0), reader.GetString(1));
        if (applied.Keys.Except(migrations.Select(m => m.Name)).Any())
            throw new InvalidOperationException("Database schema is newer than this application.");
        foreach (var migration in migrations)
        {
            if (applied.TryGetValue(migration.Name, out var existing))
            {
                if (existing != migration.Hash) throw new InvalidOperationException("An applied migration has changed.");
                continue;
            }
            await using var execute = new NpgsqlCommand(migration.Sql, connection, transaction);
            await execute.ExecuteNonQueryAsync(token);
            await using var record = new NpgsqlCommand("INSERT INTO gc.schema_migrations(version, checksum) VALUES ($1, $2)", connection, transaction);
            record.Parameters.AddWithValue(migration.Name);
            record.Parameters.AddWithValue(migration.Hash);
            await record.ExecuteNonQueryAsync(token);
        }
        await transaction.CommitAsync(token);
    }

    private static List<(string Name, string Sql, string Hash)> ReadMigrations()
    {
        var assembly = typeof(Database).Assembly;
        return assembly.GetManifestResourceNames().Where(n => n.Contains(".Migrations.") && n.EndsWith(".sql"))
            .Order(StringComparer.Ordinal).Select(name =>
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                using var reader = new StreamReader(stream);
                var sql = reader.ReadToEnd().Replace("\r\n", "\n");
                return (name, sql, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql))));
            }).ToList();
    }
}
