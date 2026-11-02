using System.Security.Cryptography;
using Npgsql;
using NpgsqlTypes;

namespace GC.Infrastructure;

internal sealed class ProjectionWork : IAsyncDisposable
{
    public NpgsqlConnection Connection { get; }
    public NpgsqlTransaction Transaction { get; }
    public Guid Server { get; private set; }
    public Guid Recording { get; private set; }
    public string Stream { get; private set; } = "";
    public int Revision { get; private set; }
    private int length, attempts;
    private string filename = "", checksum = "";
    private ProjectionWork(NpgsqlConnection connection, NpgsqlTransaction transaction) { Connection=connection; Transaction=transaction; }

    public static async Task<ProjectionWork?> Next(NpgsqlDataSource database, string[] streams, CancellationToken token)
    {
        var connection = await database.OpenConnectionAsync(token);
        ProjectionWork? work = null;
        try
        {
            work = new ProjectionWork(connection, await connection.BeginTransactionAsync(token));
            await using var query = new NpgsqlCommand("""
                SELECT server_id,recording_id,stream,revision,byte_count,archive_name,sha256,attempts
                FROM gc.ingest_receipts WHERE stream=ANY($1) AND status='received_unvalidated' AND next_attempt_at<=now()
                ORDER BY received_at,revision FOR UPDATE SKIP LOCKED LIMIT 1
                """, connection, work.Transaction);
            query.Parameters.AddWithValue(streams);
            await using var reader = await query.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) { await reader.DisposeAsync(); await work.DisposeAsync(); return null; }
            work.Server=reader.GetGuid(0); work.Recording=reader.GetGuid(1); work.Stream=reader.GetString(2);
            work.Revision=reader.GetInt32(3); work.length=reader.GetInt32(4); work.filename=reader.GetString(5);
            work.checksum=reader.GetString(6); work.attempts=reader.GetInt32(7);
            return work;
        }
        catch { if (work is not null) await work.DisposeAsync(); else await connection.DisposeAsync(); throw; }
    }

    public async Task<byte[]?> Read(string archive, CancellationToken token)
    {
        try { return await ArchiveFiles.ReadVerified(archive, filename, length, checksum, LobbyBatch.MaximumBytes(Stream), token); }
        catch (InvalidDataException) { await Finish("rejected", "archive_integrity", token); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { await Finish(attempts>=4 ? "failed" : "received_unvalidated", "archive_unavailable", token); }
        return null;
    }

    public async Task Finish(string status, string? error, CancellationToken token)
    {
        await using var update = new NpgsqlCommand("""
            UPDATE gc.ingest_receipts SET status=$5,error_code=$6,attempts=attempts+1,next_attempt_at=now()+interval '30 seconds',
                processed_at=CASE WHEN $5='received_unvalidated' THEN NULL ELSE now() END
            WHERE server_id=$1 AND stream=$2 AND recording_id=$3 AND revision=$4
            """,Connection,Transaction);
        update.Parameters.AddWithValue(Server); update.Parameters.AddWithValue(Stream); update.Parameters.AddWithValue(Recording);
        update.Parameters.AddWithValue(Revision); update.Parameters.AddWithValue(status);
        update.Parameters.AddWithValue(NpgsqlDbType.Text,(object?)error??DBNull.Value);
        await update.ExecuteNonQueryAsync(token); await Transaction.CommitAsync(token);
    }
    public async ValueTask DisposeAsync() { await Transaction.DisposeAsync(); await Connection.DisposeAsync(); }
}

public static class ArchiveFiles
{
    public static async Task<byte[]> ReadVerified(string root,string name,int length,string hash,int maximum,CancellationToken token)
    {
        if (Path.GetFileName(name)!=name || length<=0 || length>maximum) throw new InvalidDataException("Invalid archive entry.");
        await using var file=File.OpenRead(Path.Combine(root,name));
        if(file.Length!=length)throw new InvalidDataException("Archive size changed.");
        var bytes=new byte[length];await file.ReadExactlyAsync(bytes,token);
        if(Convert.ToHexString(SHA256.HashData(bytes))!=hash)throw new InvalidDataException("Archive checksum changed.");
        return bytes;
    }
}
