using System.Text.Json;
using System.Text.Json.Nodes;
using GC.Infrastructure;
using Npgsql;

internal static class MissionSelectionChecks
{
    static JsonNode Unwrap(object value) => JsonNode.Parse(string.Concat(
        JsonSerializer.SerializeToNode(value)!["chunks"]!.AsArray().Select(x => x!.GetValue<string>())))!;
    static void Check(bool condition, string name)
    { if (!condition) throw new Exception(name); Console.WriteLine("PASS: " + name); }

    public static async Task Run(NpgsqlDataSource db, Guid mission)
    {
        var description = string.Concat(Enumerable.Repeat("Long complete description 🌙 ", 500));
        await using (var update = db.CreateCommand("UPDATE gc.catalog_missions SET description=$2,metadata=jsonb_set(metadata,'{data,requiredAddonIds}','[\"6A115D5DFF7ED870\"]') WHERE id=$1"))
        { update.Parameters.AddWithValue(mission); update.Parameters.AddWithValue(description); await update.ExecuteNonQueryAsync(); }
        var detail = Unwrap((await MissionSelection.Detail(db, mission, default))!);
        Check(detail["description"]!.GetValue<string>() == description, "Admin mission description survives bounded transport without truncation");
        Check(detail["addons"]!.GetValue<string>() == "6A115D5DFF7ED870", "Admin preview retains declared addon IDs");
        var page = Unwrap(await MissionSelection.Browse(db, new("Infantry", 0), default));
        Check(page["rows"]!.AsArray().Count > 0, "Admin mission search matches tags");
        try { await MissionSelection.Browse(db, new("", -1), default); throw new Exception("Negative offset accepted"); } catch (ArgumentException) { }
        var server = Guid.NewGuid(); var request = new MissionSelection.Launch(Guid.NewGuid(), mission, "fixture-admin");
        Check(Unwrap(await MissionSelection.Queue(db, server, request, default))["status"]!.GetValue<string>() == "queued", "Mission launch queues without execution");
        await MissionSelection.Queue(db, server, request, default);
        await using (var count = db.CreateCommand("SELECT count(*) FROM gc.mission_launch_requests WHERE request_id=$1"))
        { count.Parameters.AddWithValue(request.RequestId); Check((long)(await count.ExecuteScalarAsync())! == 1, "Launch retry is idempotent"); }
        try { await MissionSelection.Queue(db, Guid.NewGuid(), request, default); throw new Exception("Cross-server retry accepted"); } catch (InvalidOperationException) { }
        var replacement = request with { RequestId = Guid.NewGuid() };
        await MissionSelection.Queue(db, server, replacement, default);
        Check(Unwrap(await MissionSelection.Queue(db, server, request, default))["status"]!.GetValue<string>() == "cancelled", "New selection supersedes only the queued request");
        await using (var manifest = db.CreateCommand("SELECT manifest FROM gc.mission_launch_requests WHERE request_id=$1"))
        {
            manifest.Parameters.AddWithValue(replacement.RequestId);
            var value = JsonNode.Parse((string)(await manifest.ExecuteScalarAsync())!)!;
            Check(value["requiredAddonIds"]!.AsArray().Count == 1 && value["dependencyResolution"]!.GetValue<string>() == "declared-only", "Launch snapshot keeps exact declared dependencies and their limitation");
        }
        await using (var hide = db.CreateCommand("UPDATE gc.catalog_missions SET available=false WHERE id=$1"))
        { hide.Parameters.AddWithValue(mission); await hide.ExecuteNonQueryAsync(); }
        try { await MissionSelection.Queue(db, server, request with { RequestId = Guid.NewGuid() }, default); throw new Exception("Unavailable launch accepted"); } catch (ArgumentException) { }
        await using (var show = db.CreateCommand("UPDATE gc.catalog_missions SET available=true WHERE id=$1"))
        { show.Parameters.AddWithValue(mission); await show.ExecuteNonQueryAsync(); }
        var claim = new MissionLaunchQueue.Claim(Guid.NewGuid());
        var assignment = JsonSerializer.SerializeToNode(await MissionLaunchQueue.Take(db, server, claim, default))!;
        Check(assignment["requestId"]!.GetValue<Guid>() == replacement.RequestId, "Worker claims the current queued request");
        var replay = JsonSerializer.SerializeToNode(await MissionLaunchQueue.Take(db, server, claim, default))!;
        Check(replay["requestId"]!.GetValue<Guid>() == replacement.RequestId, "Lost claim response returns the same assignment");
        try { await MissionLaunchQueue.Take(db, server, new(Guid.NewGuid()), default); throw new Exception("Concurrent claim accepted"); } catch (InvalidOperationException) { }
        var following = request with { RequestId = Guid.NewGuid() };
        await MissionSelection.Queue(db, server, following, default);
        Check(Unwrap(await MissionSelection.Queue(db, server, replacement, default))["status"]!.GetValue<string>() == "running", "New selection does not cancel a claimed launch");
        var result = new MissionLaunchQueue.Completion(claim.ClaimId, "completed", "Verified scenario and mods", new string('a',64), 1234);
        Check(!await MissionLaunchQueue.Finish(db, Guid.NewGuid(), replacement.RequestId, result, default), "Cross-server completion rejected");
        Check(!await MissionLaunchQueue.Finish(db, server, replacement.RequestId, result with { ClaimId = Guid.NewGuid() }, default), "Wrong claim completion rejected");
        Check(await MissionLaunchQueue.Finish(db, server, replacement.RequestId, result, default) && await MissionLaunchQueue.Finish(db, server, replacement.RequestId, result, default), "Completion retries are idempotent");
        Check(!await MissionLaunchQueue.Finish(db, server, replacement.RequestId, result with { Status = "failed" }, default), "Terminal outcome cannot be rewritten");
        var terminal = JsonSerializer.SerializeToNode(await MissionLaunchQueue.Take(db, server, claim, default))!;
        Check(terminal["status"]!.GetValue<string>() == "completed", "Recovered old claim cannot start a new request");
        var next = JsonSerializer.SerializeToNode(await MissionLaunchQueue.Take(db, server, new(Guid.NewGuid()), default))!;
        Check(next["requestId"]!.GetValue<Guid>() == following.RequestId, "Fresh claim advances after completion");
        await RuntimeRole(db, mission);
    }

    static async Task RuntimeRole(NpgsqlDataSource db, Guid mission)
    {
        var role = "gc_test_launch_" + Guid.NewGuid().ToString("N");
        await using (var create = db.CreateCommand($"CREATE ROLE {role} NOLOGIN")) await create.ExecuteNonQueryAsync();
        try
        {
            // Apply the owning deployment grants, with isolated role names, in this disposable database.
            var grants = (await File.ReadAllLinesAsync("scripts/Grant-Runtime-Access.sql"))
                .Where(x => x.StartsWith("GRANT USAGE ON SCHEMA gc TO", StringComparison.Ordinal)
                    || x.StartsWith("GRANT SELECT ON gc.catalog_sources", StringComparison.Ordinal)
                    || x.StartsWith("GRANT UPDATE(available)", StringComparison.Ordinal)
                    || x.StartsWith("GRANT SELECT,INSERT,UPDATE ON gc.mission_launch_requests", StringComparison.Ordinal)).ToArray();
            Check(grants.Length == 4, "Deployment grants include mission launch runtime access");
            foreach (var sql in grants)
            {
                await using var grant = db.CreateCommand(sql.Replace("gc_web", role).Replace("gc_worker", role));
                await grant.ExecuteNonQueryAsync();
            }
            var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("GC_TEST_ADMIN_CONNECTION")) {
                Database = new NpgsqlConnectionStringBuilder(db.ConnectionString).Database,
                Options = "-c role=" + role, Pooling = false
            };
            await using var runtime = Database.Open(connection.ConnectionString);
            var server = Guid.NewGuid(); var request = new MissionSelection.Launch(Guid.NewGuid(), mission, "runtime-admin");
            await MissionSelection.Queue(runtime, server, request, default);
            await MissionSelection.Queue(runtime, server, request, default);
            await MissionSelection.Queue(runtime, server, request with { RequestId = Guid.NewGuid() }, default);
            Check(Unwrap(await MissionSelection.Queue(runtime, server, request, default))["status"]!.GetValue<string>() == "cancelled",
                "Runtime web-role grants permit queue, retry and replacement without administrator privileges");
        }
        finally
        {
            await using (var dropOwned = db.CreateCommand($"DROP OWNED BY {role}")) await dropOwned.ExecuteNonQueryAsync();
            await using (var drop = db.CreateCommand($"DROP ROLE {role}")) await drop.ExecuteNonQueryAsync();
        }
    }
}
