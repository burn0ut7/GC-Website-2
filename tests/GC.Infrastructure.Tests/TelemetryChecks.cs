using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using GC.Infrastructure;
using Npgsql;

internal static class TelemetryChecks
{
    public static async Task Run(HttpClient client, string connection, string archive, Guid server, string worker)
    {
        var source = Environment.GetEnvironmentVariable("GC_TEST_RECORDING_DIRECTORY");
        var sourceId = Environment.GetEnvironmentVariable("GC_TEST_RECORDING_ID");
        var paths = source is null ? [] : Directory.GetFiles(source, Guid.Parse(sourceId!).ToString() + "-telemetry-*").Where(path => Path.GetExtension(path) is ".json" or ".l2t").ToArray();
        var hashes = paths.ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        var batches = paths.Length > 0 ? paths.Select(File.ReadAllBytes).ToArray() : Synthetic();
        if (source is not null && paths.Length == 0) throw new Exception("Requested capture was not found");
        batches = batches.OrderBy(bytes => { using var d = TelemetryBatch.Read(bytes); return d.RootElement.GetProperty("revision").GetInt32(); }).ToArray();
        if (batches.Length < 3) throw new Exception("Proof needs at least three batches");
        using var first = TelemetryBatch.Read(batches[0]);
        var recording = first.RootElement.GetProperty("recordingId").GetGuid();
        var totalRecords = 0; var expectedTypes = new Dictionary<string, int>(); var entities = new HashSet<string>();
        var allRecords = new List<JsonElement>();
        foreach (var bytes in batches)
        {
            using var doc = TelemetryBatch.Read(bytes);
            foreach (var record in doc.RootElement.GetProperty("records").EnumerateArray())
            {
                totalRecords++; allRecords.Add(record.Clone());
                var type = record.GetProperty("type").GetString()!;
                expectedTypes[type] = expectedTypes.GetValueOrDefault(type) + 1;
                if (type == "spawn") entities.Add(record.GetProperty("data").GetProperty("entity").GetString()!);
            }
        }
        async Task Upload(Guid id, int revision, byte[] bytes)
        {
            using var response = await client.PostAsync($"/api/v1/ingest/telemetry/{id}/{revision}", new ByteArrayContent(bytes));
            Check(response.StatusCode == HttpStatusCode.Accepted, "Capture upload accepted");
        }
        async Task<JsonElement> Summary(Guid id)
        {
            using var response = await client.GetAsync($"/api/v1/recordings/{id}");
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return doc.RootElement.Clone();
        }
        async Task WaitFor(Func<Task<bool>> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < deadline) { if (await condition()) return; await Task.Delay(200); }
            throw new Exception("Timed out waiting for telemetry worker");
        }
        var heldRevision = batches.Length / 2;
        // Reversed arrival is intentional; closure arrives before the start and a middle batch is absent.
        for (var i = batches.Length - 1; i >= 0; i--) if (i != heldRevision) await Upload(recording, i, batches[i]);
        var runner = StartWorker(worker, connection, archive);
        try
        {
            await WaitFor(async () => (await Summary(recording)).GetProperty("validatedBatches").GetInt64() > 0);
            if (batches.Length > 8) Check((await Summary(recording)).GetProperty("validatedBatches").GetInt64() < batches.Length - 1, "Restart begins with pending work");
            await runner.Stop(); // Simulate process loss with pending receipts still in the database.
            runner = StartWorker(worker, connection, archive);
            await WaitFor(async () => (await Summary(recording)).GetProperty("validatedBatches").GetInt64() == batches.Length - 1);
            var incomplete = await Summary(recording);
            Check(!incomplete.GetProperty("allBatchesPresent").GetBoolean(), "Missing batch is visible");
            await Upload(recording, heldRevision, batches[heldRevision]);
            await WaitFor(async () => (await Summary(recording)).GetProperty("allBatchesPresent").GetBoolean());
            var complete = await Summary(recording);
            Check(complete.GetProperty("status").GetString() == "complete", "Closed recording is complete");
            Check(complete.GetProperty("recordCount").GetInt64() == totalRecords, "Projected record count matches source");
            Check(complete.GetProperty("spawnedEntityCount").GetInt64() == entities.Count, "Entity count matches source");
            foreach (var (type, count) in expectedTypes) Check(complete.GetProperty("recordTypes").GetProperty(type).GetInt64() == count, "Event totals match source");
            using var last = TelemetryBatch.Read(batches[^1]);
            var duration = last.RootElement.GetProperty("untilMs").GetDouble() - first.RootElement.GetProperty("fromMs").GetDouble();
            Check(Math.Abs(complete.GetProperty("durationMs").GetDouble() - duration) < 0.001, "Duration matches source clock");
            await Upload(recording, 0, batches[0]);
            Check((await Summary(recording)).GetProperty("recordCount").GetInt64() == totalRecords, "Duplicate upload adds no records");

            var middle = allRecords[allRecords.Count / 2].GetProperty("timeMs").GetDouble();
            var from = middle; var until = middle + 1000;
            var expected = allRecords.Where(r => r.GetProperty("timeMs").GetDouble() >= from && r.GetProperty("timeMs").GetDouble() <= until).ToArray();
            var received = new List<JsonElement>(); var after = 0; var pages = 0; var scanned = 0;
            while (true)
            {
                var url = FormattableString.Invariant($"/api/v1/recordings/{recording}/records?fromMs={from}&untilMs={until}&afterSequence={after}&limit=17");
                using var response = await client.GetAsync(url); response.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                received.AddRange(doc.RootElement.GetProperty("records").EnumerateArray().Select(r => r.Clone()));
                scanned += doc.RootElement.GetProperty("scannedBatches").GetInt32();
                var next = doc.RootElement.GetProperty("nextAfterSequence");
                if (next.ValueKind == JsonValueKind.Null) break;
                Check(next.GetInt32() > after && ++pages < 1000, "Pagination advances"); after = next.GetInt32();
            }
            Check(received.Count == expected.Length, "Window count matches source");
            for (var i = 0; i < expected.Length; i++) Check(JsonElement.DeepEquals(expected[i], received[i]), "Window data matches source");
            // The range index must not fetch every recording batch for this small window.
            if (batches.Length > 10) Check(scanned < batches.Length, "Small window avoids full recording scan");

            var broadFrom = first.RootElement.GetProperty("fromMs").GetDouble();
            var broadUntil = broadFrom + 60000; var broadAfter = 0; var broadCount = 0; var sawEight = false;
            for (var page = 0; page < 1000; page++)
            {
                using var response = await client.GetAsync(FormattableString.Invariant($"/api/v1/recordings/{recording}/records?fromMs={broadFrom}&untilMs={broadUntil}&afterSequence={broadAfter}&limit=500"));
                response.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                var read = doc.RootElement.GetProperty("scannedBatches").GetInt32(); Check(read<=8, "Chunk-read bound enforced"); sawEight |= read==8;
                broadCount += doc.RootElement.GetProperty("records").GetArrayLength();
                var next = doc.RootElement.GetProperty("nextAfterSequence"); if (next.ValueKind == JsonValueKind.Null) break;
                Check(next.GetInt32()>broadAfter, "Chunk cursor advances"); broadAfter=next.GetInt32();
            }
            Check(broadCount==allRecords.Count(r => r.GetProperty("timeMs").GetDouble()>=broadFrom && r.GetProperty("timeMs").GetDouble()<=broadUntil), "Multi-chunk pagination returns every expected record");
            if (paths.Length==0) Check(sawEight, "Eight-chunk continuation exercised");

            var archived = Directory.GetFiles(archive, $"{server:N}-telemetry-{recording:N}-0-*.batch").Single();
            var preserved = await File.ReadAllBytesAsync(archived);
            try
            {
                await File.WriteAllBytesAsync(archived, [0]);
                using var response = await client.GetAsync(FormattableString.Invariant($"/api/v1/recordings/{recording}/records?fromMs={broadFrom}&untilMs={broadUntil}"));
                Check(response.StatusCode==HttpStatusCode.ServiceUnavailable, "Archive corruption fails closed");
                using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Check(error.RootElement.GetProperty("error").GetString()=="archive_integrity", "Archive integrity failure is explicit");
            }
            finally { await File.WriteAllBytesAsync(archived, preserved); }

            var extraId = Guid.NewGuid(); var extra = CloneBatch(batches[0]); extra["recordingId"]=extraId; extra["extra"]="\0";
            await Upload(extraId,0,JsonSerializer.SerializeToUtf8Bytes(extra));
            var binaryId = Guid.NewGuid(); var binaryRoot = CloneBatch(batches[0]); binaryRoot["recordingId"] = binaryId;
            using var binaryDoc = JsonDocument.Parse(binaryRoot.ToJsonString());
            await Upload(binaryId, 0, TelemetryBinary.Encode(binaryDoc.RootElement));
            var corruptId = Guid.NewGuid(); await Upload(corruptId, 0, [1,2,3]);
            var mismatchId = Guid.NewGuid(); await Upload(mismatchId, 0, batches[0]);
            var conflictId = Guid.NewGuid(); var initial = CloneBatch(batches[0]); initial["recordingId"] = conflictId;
            await Upload(conflictId, 0, JsonSerializer.SerializeToUtf8Bytes(initial));
            await WaitFor(async () => (await Summary(conflictId)).GetProperty("validatedBatches").GetInt64() == 1);
            var conflicting = CloneBatch(batches[1]); conflicting["recordingId"] = conflictId; conflicting["world"] = "different-world";
            await Upload(conflictId, 1, JsonSerializer.SerializeToUtf8Bytes(conflicting));
            await WaitFor(async () =>
            {
                var a = await Summary(corruptId); var b = await Summary(mismatchId); var c = await Summary(conflictId);
                return a.GetProperty("status").GetString() == "degraded" && b.GetProperty("status").GetString() == "degraded" &&
                    c.GetProperty("status").GetString() == "degraded" && (await Summary(binaryId)).GetProperty("validatedBatches").GetInt64() == 1 &&
                    (await Summary(extraId)).GetProperty("validatedBatches").GetInt64() == 1;
            });
            var authorization = client.DefaultRequestHeaders.Authorization; client.DefaultRequestHeaders.Authorization = null;
            using (var denied = await client.GetAsync($"/api/v1/recordings/{recording}")) Check(denied.StatusCode == HttpStatusCode.Unauthorized, "Recording API requires machine authorization");
            client.DefaultRequestHeaders.Authorization = authorization;
            foreach (var (path, hash) in hashes) Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) == hash, "Source capture unchanged");
            Console.WriteLine($"PASS: {(paths.Length > 0 ? "real copied" : "synthetic")} telemetry: {batches.Length} batches, {totalRecords} records, {entities.Count} entities, {duration:F3} ms; window {received.Count} records/{scanned} chunk reads. Out-of-order, missing, duplicate, binary, malformed, identity/metadata conflicts, worker restart and source integrity verified.");
        }
        finally { await runner.Stop(); }
    }

    private static WorkerProcess StartWorker(string dll, string connection, string archive)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true };
        start.ArgumentList.Add(dll); start.Environment["ConnectionStrings__Gc"] = connection;
        start.Environment["Telemetry__Enabled"] = "true"; start.Environment["Ingest__ArchivePath"] = archive;
        return new WorkerProcess(Process.Start(start)!);
    }
    private sealed class WorkerProcess(Process process)
    {
        private readonly Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        private readonly Task<string> stderr = process.StandardError.ReadToEndAsync();
        private bool stopped;
        public async Task Stop()
        {
            if (stopped) return; stopped=true;
            if (!process.HasExited) process.Kill(entireProcessTree:true);
            await process.WaitForExitAsync(); await Task.WhenAll(stdout,stderr); process.Dispose();
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    private static JsonNode CloneBatch(byte[] bytes)
    {
        using var document = TelemetryBatch.Read(bytes);
        return JsonNode.Parse(document.RootElement.GetRawText())!;
    }

    private static byte[][] Synthetic()
    {
        var recording = Guid.NewGuid(); var mission = Guid.NewGuid();
        const int count = 12;
        return Enumerable.Range(0,count).Select(revision =>
        {
            var records = Enumerable.Range(revision*2+1,2).Select(sequence => new {
                sequence, timeMs=sequence*1000, type="spawn", data=new {entity="entity-"+sequence, kind="ai", prefab="test",playerIdentity="",faction="test",position=new[]{1,2,3},anglesDegrees=new[]{0,0,0}}
            });
            return JsonSerializer.SerializeToUtf8Bytes(new {schemaVersion=2,kind="telemetry",missionId=mission,recordingId=recording,revision,startedAt=0,world="test",gameVersion="test",assetManifestId="test",clock="session-world-ms",coordinates="enfusion-world-metres",sampleHz=5,firstSequence=revision*2+1,fromMs=(revision*2+1)*1000,untilMs=(revision*2+2)*1000,closed=revision==count-1,stopReason=revision==count-1?"mission-end":"",droppedRecords=0,coverage=new{actors="players-and-ai",pose="inferred",shots=false,impacts=false,damage=false,grenades=false,vehicles=false,gear=false},records});
        }).ToArray();
    }
}
