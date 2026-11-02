using System.Security.Cryptography;
using Npgsql;

namespace GC.Infrastructure;

public static class Intake
{
    public const int MaximumBytes = 1024 * 1024;
    public static bool ValidStream(string stream) => stream is "stats" or "session" or "history" or "telemetry";

    public static async Task<bool> Receive(NpgsqlDataSource database, string archiveRoot, Guid server, string stream,
        Guid recording, int revision, byte[] bytes, CancellationToken token)
    {
        if (!ValidStream(stream) || server == Guid.Empty || recording == Guid.Empty || revision < 0 || bytes.Length == 0 || bytes.Length > LobbyBatch.MaximumBytes(stream))
            throw new ArgumentException("Invalid batch envelope.");
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        await using (var prior = database.CreateCommand("SELECT sha256 FROM gc.ingest_receipts WHERE server_id=$1 AND stream=$2 AND recording_id=$3 AND revision=$4"))
        {
            prior.Parameters.AddWithValue(server); prior.Parameters.AddWithValue(stream);
            prior.Parameters.AddWithValue(recording); prior.Parameters.AddWithValue(revision);
            var previous = await prior.ExecuteScalarAsync(token);
            if (previous is string priorHash && priorHash != hash) return false;
        }
        var archiveName = $"{server:N}-{stream}-{recording:N}-{revision}-{hash}.batch";
        Directory.CreateDirectory(archiveRoot);
        var destination = Path.Combine(archiveRoot, archiveName);
        var temporary = Path.Combine(archiveRoot, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await file.WriteAsync(bytes, token);
                file.Flush(flushToDisk: true);
            }
            try { File.Move(temporary, destination); }
            catch (IOException) when (File.Exists(destination))
            {
                await using var existing = File.OpenRead(destination);
                if (Convert.ToHexString(await SHA256.HashDataAsync(existing, token)) != hash)
                    throw new IOException("Existing archive failed integrity verification.");
            }
            // A crash before this insert leaves a recoverable orphan, never an acknowledged missing file.
            await using var insert = database.CreateCommand("""
                INSERT INTO gc.ingest_receipts(server_id, stream, recording_id, revision, sha256, byte_count, archive_name)
                VALUES ($1,$2,$3,$4,$5,$6,$7) ON CONFLICT DO NOTHING
                """);
            insert.Parameters.AddWithValue(server); insert.Parameters.AddWithValue(stream);
            insert.Parameters.AddWithValue(recording); insert.Parameters.AddWithValue(revision);
            insert.Parameters.AddWithValue(hash); insert.Parameters.AddWithValue(bytes.Length); insert.Parameters.AddWithValue(archiveName);
            await insert.ExecuteNonQueryAsync(token);
            await using var verify = database.CreateCommand("SELECT sha256 FROM gc.ingest_receipts WHERE server_id=$1 AND stream=$2 AND recording_id=$3 AND revision=$4");
            verify.Parameters.AddWithValue(server); verify.Parameters.AddWithValue(stream);
            verify.Parameters.AddWithValue(recording); verify.Parameters.AddWithValue(revision);
            return (string?)await verify.ExecuteScalarAsync(token) == hash;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
