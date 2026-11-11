using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace GC.Infrastructure;

/// <summary>Server-only admin catalogue and durable requests. Execution belongs to a future worker.</summary>
public static class MissionSelection
{
    public sealed record Query(string Search, int Offset);
    public sealed record Launch(Guid RequestId, Guid MissionId, string RequesterIdentity);

    // Each string is an independently bounded reliable game RPC unit; never split a surrogate pair.
    public static object Envelope(object value)
    {
        var json = JsonSerializer.Serialize(value);
        var chunks = new List<string>();
        for (var start = 0; start < json.Length;)
        {
            var length = Math.Min(800, json.Length - start);
            if (char.IsHighSurrogate(json[start + length - 1])) length--;
            chunks.Add(json.Substring(start, length));
            start += length;
        }
        return new { version = 1, chunks };
    }

    public static async Task<object> Browse(NpgsqlDataSource db, Query filter, CancellationToken ct)
    {
        if (filter.Search is null || filter.Search.Length > 200 || filter.Offset < 0 || filter.Offset > 100000)
            throw new ArgumentException("Invalid filter");
        await using var query = db.CreateCommand("""
            SELECT id,name,max_players,metadata->'data'->>'minPlayers',tags,available,listed
            FROM gc.catalog_missions
            WHERE $1='' OR strpos(lower(name||' '||array_to_string(tags,' ')||' '||coalesce(max_players::text,'')||' '||coalesce(metadata->'data'->>'minPlayers','')),lower($1))>0
            ORDER BY name,id LIMIT 26 OFFSET $2
            """);
        query.Parameters.AddWithValue(filter.Search.Trim()); query.Parameters.AddWithValue(filter.Offset);
        var rows = new List<string>();
        await using var reader = await query.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (rows.Count == 25) return Envelope(new { rows, nextOffset = filter.Offset + 25 });
            rows.Add(JsonSerializer.Serialize(new {
                id = reader.GetGuid(0), name = reader.GetString(1),
                maxPlayers = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                minPlayers = !reader.IsDBNull(3) && int.TryParse(reader.GetString(3), out var minimum) ? minimum : 0,
                tags = string.Join(" · ", reader.GetFieldValue<string[]>(4)),
                available = reader.GetBoolean(5), listed = reader.GetBoolean(6)
            }));
        }
        return Envelope(new { rows, nextOffset = -1 });
    }

    public static async Task<object?> Detail(NpgsqlDataSource db, Guid id, CancellationToken ct)
    {
        var detail = await MissionCatalog.Detail(db, id, ct);
        if (detail is null) return null;
        var value = JsonSerializer.SerializeToElement(detail);
        var metadata = value.GetProperty("metadata");
        return Envelope(new {
            id, name = value.GetProperty("name").GetString(), description = value.GetProperty("description").GetString(),
            tags = string.Join(" · ", value.GetProperty("tags").Deserialize<string[]>() ?? []),
            available = value.GetProperty("available").GetBoolean(),
            thumbnail = metadata.TryGetProperty("previewResource", out var preview) ? preview.GetString() : "",
            addons = metadata.TryGetProperty("requiredAddonIds", out var addons) ? string.Join(", ", addons.Deserialize<string[]>() ?? []) : ""
        });
    }

    public static async Task<object> Queue(NpgsqlDataSource db, Guid server, Launch request, CancellationToken ct)
    {
        if (request.RequestId == Guid.Empty || request.MissionId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.RequesterIdentity) || request.RequesterIdentity.Length > 128)
            throw new ArgumentException("Invalid request");
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        // Serialize this server's queue and retries without cancelling an execution already claimed by a worker.
        await using (var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1,0))", connection, transaction))
        { gate.Parameters.AddWithValue(server.ToString()); await gate.ExecuteNonQueryAsync(ct); }
        await using (var prior = new NpgsqlCommand("SELECT server_id,catalog_id,requester_identity,status FROM gc.mission_launch_requests WHERE request_id=$1", connection, transaction))
        {
            prior.Parameters.AddWithValue(request.RequestId);
            await using var previous = await prior.ExecuteReaderAsync(ct);
            if (await previous.ReadAsync(ct))
            {
                if (previous.GetGuid(0) != server || previous.GetGuid(1) != request.MissionId || previous.GetString(2) != request.RequesterIdentity)
                    throw new InvalidOperationException("Request identity conflict");
                return Envelope(new { status = previous.GetString(3), requestId = request.RequestId });
            }
        }
        string scenario, metadata;
        await using (var mission = new NpgsqlCommand("SELECT scenario_guid,scenario_path,metadata->'data' FROM gc.catalog_missions WHERE id=$1 AND available FOR SHARE", connection, transaction))
        {
            mission.Parameters.AddWithValue(request.MissionId);
            await using var reader = await mission.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) throw new ArgumentException("Mission unavailable");
            var guid = reader.GetString(0); var path = reader.GetString(1);
            if (guid.Length != 16 || !guid.All(Uri.IsHexDigit) || !path.EndsWith(".conf", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Mission has no valid scenario");
            scenario = "{" + guid + "}" + path; metadata = reader.GetString(2);
        }
        using var parsed = JsonDocument.Parse(metadata);
        if (parsed.RootElement.TryGetProperty("missionType", out var type) && type.GetString() == "BROKEN")
            throw new ArgumentException("Mission is marked broken");
        var addons = parsed.RootElement.TryGetProperty("requiredAddonIds", out var declared)
            ? declared.Deserialize<string[]>() ?? [] : [];
        if (addons.Length > 128 || addons.Any(x => x is null || x.Length != 16 || !x.All(Uri.IsHexDigit)))
            throw new ArgumentException("Invalid addon metadata");
        var manifest = JsonSerializer.Serialize(new {
            scenario, requiredAddonIds = addons.Select(x => x.ToUpperInvariant()).Distinct().ToArray(),
            dependencyResolution = "declared-only", metadata = parsed.RootElement,
            execution = "pending-future-restart-worker"
        });
        await using (var cancel = new NpgsqlCommand("UPDATE gc.mission_launch_requests SET status='cancelled' WHERE server_id=$1 AND status='queued'", connection, transaction))
        { cancel.Parameters.AddWithValue(server); await cancel.ExecuteNonQueryAsync(ct); }
        await using (var insert = new NpgsqlCommand("INSERT INTO gc.mission_launch_requests(request_id,server_id,catalog_id,requester_identity,manifest) VALUES($1,$2,$3,$4,$5)", connection, transaction))
        {
            insert.Parameters.AddWithValue(request.RequestId); insert.Parameters.AddWithValue(server);
            insert.Parameters.AddWithValue(request.MissionId); insert.Parameters.AddWithValue(request.RequesterIdentity);
            insert.Parameters.AddWithValue(NpgsqlDbType.Jsonb, manifest); await insert.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return Envelope(new { status = "queued", requestId = request.RequestId });
    }
}
