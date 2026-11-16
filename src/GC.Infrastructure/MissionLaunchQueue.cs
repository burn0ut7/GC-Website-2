using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace GC.Infrastructure;

/// <summary>Server-scoped durable handoff. Claims never expire into a duplicate server restart.</summary>
public static class MissionLaunchQueue
{
    public sealed record Claim(Guid ClaimId);
    public sealed record Completion(Guid ClaimId, string Status, string Summary, string PlanSha256, int? ProcessId);

    public static async Task<object?> Take(NpgsqlDataSource db, Guid server, Claim request, CancellationToken ct)
    {
        if (request.ClaimId == Guid.Empty) throw new ArgumentException("Claim identity required");
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await using (var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1,0))", connection, tx))
        { gate.Parameters.AddWithValue(server.ToString()); await gate.ExecuteNonQueryAsync(ct); }
        // A lost response must return the same assignment, including terminal outcomes.
        await using (var prior = new NpgsqlCommand("SELECT request_id,catalog_id,status,manifest FROM gc.mission_launch_requests WHERE server_id=$1 AND claim_id=$2", connection, tx))
        {
            prior.Parameters.AddWithValue(server); prior.Parameters.AddWithValue(request.ClaimId);
            await using var reader = await prior.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct)) return Assignment(reader, request.ClaimId);
        }
        await using (var active = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM gc.mission_launch_requests WHERE server_id=$1 AND status='running')", connection, tx))
        {
            active.Parameters.AddWithValue(server);
            if ((bool)(await active.ExecuteScalarAsync(ct))!) throw new InvalidOperationException("Another worker owns this server's request");
        }
        object? result = null;
        await using (var claim = new NpgsqlCommand("""
            UPDATE gc.mission_launch_requests SET status='running',claim_id=$2,claimed_at=now()
            WHERE request_id=(SELECT request_id FROM gc.mission_launch_requests WHERE server_id=$1 AND status='queued' ORDER BY requested_at,request_id LIMIT 1)
            RETURNING request_id,catalog_id,status,manifest
            """, connection, tx))
        {
            claim.Parameters.AddWithValue(server); claim.Parameters.AddWithValue(request.ClaimId);
            await using var reader = await claim.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct)) result = Assignment(reader, request.ClaimId);
        }
        await tx.CommitAsync(ct);
        return result;
    }

    private static object Assignment(NpgsqlDataReader reader, Guid claim) => new {
        requestId = reader.GetGuid(0), missionId = reader.GetGuid(1), status = reader.GetString(2), claimId = claim,
        manifest = JsonSerializer.Deserialize<JsonElement>(reader.GetString(3))
    };

    public static async Task<bool> Finish(NpgsqlDataSource db, Guid server, Guid request, Completion result, CancellationToken ct)
    {
        if (request == Guid.Empty || result.ClaimId == Guid.Empty || result.Status is not ("completed" or "failed") ||
            string.IsNullOrWhiteSpace(result.Summary) || result.Summary.Length > 2048 || result.Summary.Any(char.IsControl) ||
            result.PlanSha256 is null || (result.PlanSha256.Length != 0 && (result.PlanSha256.Length != 64 || !result.PlanSha256.All(Uri.IsHexDigit))) ||
            result.ProcessId is <= 0 || (result.Status == "completed" && (result.ProcessId is null || result.PlanSha256.Length != 64)))
            throw new ArgumentException("Invalid launch outcome");
        var outcome = JsonSerializer.Serialize(new { summary = result.Summary, planSha256 = result.PlanSha256, processId = result.ProcessId });
        await using var update = db.CreateCommand("""
            UPDATE gc.mission_launch_requests SET status=$4,outcome=$5,finished_at=coalesce(finished_at,now())
            WHERE server_id=$1 AND request_id=$2 AND claim_id=$3
              AND (status='running' OR (status=$4 AND outcome=$5))
            """);
        update.Parameters.AddWithValue(server); update.Parameters.AddWithValue(request); update.Parameters.AddWithValue(result.ClaimId);
        update.Parameters.AddWithValue(result.Status); update.Parameters.AddWithValue(NpgsqlDbType.Jsonb, outcome);
        return await update.ExecuteNonQueryAsync(ct) == 1;
    }
}
