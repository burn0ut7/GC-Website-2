using System.Text;
using System.Text.Json;
using Npgsql;

namespace GC.Infrastructure;

public static class Recordings
{
    public static async Task<object?> Summary(NpgsqlDataSource database, Guid server, Guid recording, CancellationToken token)
    {
        await using var connection = await database.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, token);
        var receipts = new Dictionary<string, long>();
        await using (var query = new NpgsqlCommand("SELECT status,count(*) FROM gc.ingest_receipts WHERE server_id=$1 AND recording_id=$2 AND stream='telemetry' GROUP BY status", connection, transaction))
        {
            query.Parameters.AddWithValue(server); query.Parameters.AddWithValue(recording);
            await using var reader = await query.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) receipts.Add(reader.GetString(0), reader.GetInt64(1));
        }
        if (receipts.Count == 0) return null;
        var count = 0L; var allPresent = false; var closed = false; var drops = 0;
        double? from = null, until = null; long records = 0;
        await using (var query = new NpgsqlCommand("""
            SELECT count(*), min(revision), max(revision), min(from_ms), max(until_ms),
                coalesce(sum(record_count),0), bool_or(closed), coalesce(max(dropped_records),0)
            FROM gc.telemetry_batches WHERE server_id=$1 AND recording_id=$2
            """, connection, transaction))
        {
            query.Parameters.AddWithValue(server); query.Parameters.AddWithValue(recording);
            await using var reader = await query.ExecuteReaderAsync(token); await reader.ReadAsync(token);
            count = reader.GetInt64(0);
            if (count > 0)
            {
                from = reader.GetDouble(3); until = reader.GetDouble(4); records = reader.GetInt64(5);
                closed = reader.GetBoolean(6); drops = reader.GetInt32(7);
                allPresent = closed && reader.GetInt32(1) == 0 && (long)reader.GetInt32(2) + 1 == count;
            }
        }
        JsonElement? metadata = null;
        await using (var query = new NpgsqlCommand("SELECT header FROM gc.telemetry_batches WHERE server_id=$1 AND recording_id=$2 ORDER BY revision LIMIT 1", connection, transaction))
        {
            query.Parameters.AddWithValue(server); query.Parameters.AddWithValue(recording);
            if (await query.ExecuteScalarAsync(token) is string header)
            {
                using var doc = JsonDocument.Parse(header);
                metadata = doc.RootElement.Clone();
            }
        }
        var types = new Dictionary<string, long>();
        await using (var query = new NpgsqlCommand("""
            SELECT item.key, sum(item.value::bigint)::bigint FROM gc.telemetry_batches b
            CROSS JOIN LATERAL jsonb_each_text(b.type_counts) item
            WHERE server_id=$1 AND recording_id=$2 GROUP BY item.key
            """, connection, transaction))
        {
            query.Parameters.AddWithValue(server); query.Parameters.AddWithValue(recording);
            await using var reader = await query.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) types.Add(reader.GetString(0), reader.GetInt64(1));
        }
        await using var entities = new NpgsqlCommand("""
            SELECT count(DISTINCT entity) FROM gc.telemetry_batches b CROSS JOIN LATERAL unnest(b.spawned_entities) entity
            WHERE server_id=$1 AND recording_id=$2
            """, connection, transaction);
        entities.Parameters.AddWithValue(server); entities.Parameters.AddWithValue(recording);
        var entityCount = (long)(await entities.ExecuteScalarAsync(token))!;
        var pending = receipts.GetValueOrDefault("received_unvalidated");
        var rejected = receipts.GetValueOrDefault("rejected") + receipts.GetValueOrDefault("failed");
        return new { recordingId = recording, allBatchesPresent = allPresent, closed, droppedRecords = drops,
            status = pending > 0 ? "pending" : rejected > 0 || drops > 0 || types.GetValueOrDefault("gap") > 0 ? "degraded" : allPresent ? "complete" : "incomplete",
            validatedBatches = count, receipts, fromMs = from, untilMs = until, durationMs = until - from,
            recordCount = records, spawnedEntityCount = entityCount, recordTypes = types, metadata };
    }

    public static async Task<object> Range(NpgsqlDataSource database, string archive, Guid server, Guid recording,
        double from, double until, int afterSequence, int limit, CancellationToken token, string? type = null)
    {
        if (type?.Length > 64) throw new ArgumentException("Invalid event type.");
        if (!double.IsFinite(from) || !double.IsFinite(until) || from < 0 || until < from || until - from > 60000 || afterSequence < 0 || limit is < 1 or > 500)
            throw new ArgumentException("Range must be at most 60 seconds with 1..500 records.");
        var chunks = new List<(string Name, string Hash, int Bytes, int LastSequence)>();
        await using (var command = database.CreateCommand("""
            SELECT r.archive_name,r.sha256,r.byte_count,b.first_sequence+b.record_count-1
            FROM gc.telemetry_batches b JOIN gc.ingest_receipts r
              ON r.server_id=b.server_id AND r.recording_id=b.recording_id AND r.revision=b.revision AND r.stream='telemetry'
            WHERE b.server_id=$1 AND b.recording_id=$2 AND b.until_ms >= $3 AND b.from_ms <= $4
              AND b.first_sequence+b.record_count-1 > $5
            ORDER BY b.revision LIMIT 9
            """))
        {
            command.Parameters.AddWithValue(server); command.Parameters.AddWithValue(recording);
            command.Parameters.AddWithValue(from); command.Parameters.AddWithValue(until); command.Parameters.AddWithValue(afterSequence);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) chunks.Add((reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3)));
        }
        var records = new List<JsonElement>(); var responseBytes = 0; var scanned = 0; var cursor = afterSequence;
        foreach (var chunk in chunks.Take(8))
        {
            using var batch = TelemetryBatch.Read(await TelemetryProjection.ReadVerified(archive, chunk.Name, chunk.Bytes, chunk.Hash, token));
            scanned++;
            foreach (var record in batch.RootElement.GetProperty("records").EnumerateArray())
            {
                var sequence = record.GetProperty("sequence").GetInt32();
                if (sequence <= afterSequence) continue;
                var time = record.GetProperty("timeMs").GetDouble();
                if (time >= from && time <= until && (string.IsNullOrEmpty(type) || record.GetProperty("type").GetString() == type))
                {
                    var bytes = Encoding.UTF8.GetByteCount(record.GetRawText());
                    if (records.Count >= limit || responseBytes + bytes > 2 * 1024 * 1024)
                        return Result(cursor);
                    records.Add(record.Clone()); responseBytes += bytes;
                }
                cursor = sequence;
            }
            cursor = Math.Max(cursor, chunk.LastSequence);
        }
        return Result(chunks.Count > 8 ? cursor : null);

        object Result(int? next) => new { recordingId = recording, fromMs = from, untilMs = until,
            records, nextAfterSequence = next, scannedBatches = scanned,
            semantics = "Recorded events only; no interpolation, seek baseline or inferred continuity." };
    }
}
