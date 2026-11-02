using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace GC.Infrastructure;

public static class TelemetryProjection
{
    public static async Task<bool> ProjectNext(NpgsqlDataSource database, string archive, CancellationToken token)
    {
        await using var work = await ProjectionWork.Next(database, ["telemetry"], token);
        if (work is null) return false;
        var connection=work.Connection; var transaction=work.Transaction;
        var server=work.Server; var recording=work.Recording; var revision=work.Revision;
        Task Finish(string status,string? error) => work.Finish(status,error,token);
        var bytes=await work.Read(archive,token);
        if(bytes is null)return true;
        JsonDocument document;
        try { document = TelemetryBatch.Read(bytes); }
        catch (Exception error) when (IsInvalidBatch(error)) { await Finish("rejected", "invalid_batch"); return true; }
        using (document)
        {
            var root = document.RootElement;
            if (root.GetProperty("recordingId").GetGuid() != recording || root.GetProperty("revision").GetInt32() != revision)
            { await Finish("rejected", "envelope_mismatch"); return true; }
            var records = root.GetProperty("records");
            await using (var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1,0))", connection, transaction))
            {
                gate.Parameters.AddWithValue($"telemetry:{server}:{recording}");
                await gate.ExecuteNonQueryAsync(token);
            }
            var neighbors = new List<(JsonDocument Header, int Count)>();
            try
            {
                await using (var query = new NpgsqlCommand("""
                    SELECT header, record_count FROM gc.telemetry_batches WHERE server_id=$1 AND recording_id=$2 AND revision IN (
                        (SELECT max(revision) FROM gc.telemetry_batches WHERE server_id=$1 AND recording_id=$2 AND revision<$3),
                        (SELECT min(revision) FROM gc.telemetry_batches WHERE server_id=$1 AND recording_id=$2 AND revision>$3))
                    """, connection, transaction))
                {
                    query.Parameters.AddWithValue(server); query.Parameters.AddWithValue(recording); query.Parameters.AddWithValue(revision);
                    await using var reader = await query.ExecuteReaderAsync(token);
                    while (await reader.ReadAsync(token)) neighbors.Add((JsonDocument.Parse(reader.GetString(0)), reader.GetInt32(1)));
                }
                try
                {
                    foreach (var neighbor in neighbors) TelemetryContinuity.Validate(root, records.GetArrayLength(), neighbor.Header.RootElement, neighbor.Count);
                }
                catch (InvalidDataException) { await Finish("rejected", "continuity_conflict"); return true; }
            }
            finally { foreach (var neighbor in neighbors) neighbor.Header.Dispose(); }
            var header = JsonSerializer.Serialize(root.EnumerateObject().Where(p => p.Name != "records").ToDictionary(p => p.Name, p => p.Value));
            var types = records.EnumerateArray().GroupBy(r => r.GetProperty("type").GetString()!).ToDictionary(g => g.Key, g => g.Count());
            var entities = records.EnumerateArray().Where(r => r.GetProperty("type").GetString() == "spawn")
                .Select(r => r.GetProperty("data").GetProperty("entity").GetString()!).Distinct().ToArray();
            await using var insert = new NpgsqlCommand("""
                INSERT INTO gc.telemetry_batches(server_id,recording_id,revision,header,from_ms,until_ms,record_count,first_sequence,closed,dropped_records,type_counts,spawned_entities,mission_id)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13)
                """, connection, transaction);
            insert.Parameters.AddWithValue(server); insert.Parameters.AddWithValue(recording); insert.Parameters.AddWithValue(revision);
            insert.Parameters.AddWithValue(NpgsqlDbType.Text, header);
            insert.Parameters.AddWithValue(root.GetProperty("fromMs").GetDouble()); insert.Parameters.AddWithValue(root.GetProperty("untilMs").GetDouble());
            insert.Parameters.AddWithValue(records.GetArrayLength()); insert.Parameters.AddWithValue(root.GetProperty("firstSequence").GetInt32());
            insert.Parameters.AddWithValue(root.GetProperty("closed").GetBoolean()); insert.Parameters.AddWithValue(root.GetProperty("droppedRecords").GetInt32());
            insert.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(types)); insert.Parameters.AddWithValue(entities);
            insert.Parameters.AddWithValue(root.GetProperty("missionId").GetGuid());
            await insert.ExecuteNonQueryAsync(token);
            await Finish("projected", null);
        }
        return true;
    }

    public static Task<byte[]> ReadVerified(string root, string name, int length, string hash, CancellationToken token)
        => ArchiveFiles.ReadVerified(root,name,length,hash,TelemetryContract.MaxBatchBytes,token);
    private static bool IsInvalidBatch(Exception error) => error is JsonException or InvalidDataException or FormatException
        or InvalidOperationException or KeyNotFoundException or OverflowException or ArgumentException;
}
