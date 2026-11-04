using System.Text.Json;
using System.Text.Json.Nodes;

internal static class VoiceChecks
{
    public static byte[] Batch(Guid mission, Guid recording)
    {
        var batch = JsonNode.Parse(TelemetryBinary.Decode(PipelineChecks.Telemetry(mission, recording)))!;
        batch["firstSequence"] = 1; batch["fromMs"] = 0; batch["untilMs"] = 1000;
        var records = new JsonArray();
        var types = new[] { "voice-start", "voice-refresh", "voice-stop" };
        for (var i = 0; i < types.Length; i++)
            records.Add(new JsonObject {
                ["sequence"] = i + 1, ["timeMs"] = i * 500, ["type"] = types[i],
                ["data"] = new JsonObject { ["entity"] = "entity-1", ["channel"] = "radio", ["source"] = "client-capture", ["validForMs"] = i == 2 ? 0 : 1500 }
            });
        batch["records"] = records;
        return JsonSerializer.SerializeToUtf8Bytes(batch);
    }

    public static void Run()
    {
        var bytes = Batch(Guid.NewGuid(), Guid.NewGuid());
        using var json = JsonDocument.Parse(bytes);
        TelemetryContract.Validate(json.RootElement);
        var binary = TelemetryBinary.Encode(json.RootElement);
        var decoded = TelemetryBinary.Decode(binary);
        using var roundTrip = JsonDocument.Parse(decoded);
        TelemetryContract.Validate(roundTrip.RootElement);
        if (roundTrip.RootElement.GetProperty("records").GetArrayLength() != 3) throw new Exception("Voice records lost in L2TB");
        foreach (var change in new Action<JsonNode>[] {
            n => n["channel"] = "invented", n => n["source"] = "authoritative",
            n => n["validForMs"] = 999999, n => n["entity"] = ""
        }) {
            var bad = JsonNode.Parse(bytes)!; change(bad["records"]![0]!["data"]!);
            using var doc = JsonDocument.Parse(bad.ToJsonString());
            var rejected = false;
            try { TelemetryContract.Validate(doc.RootElement); } catch { rejected = true; }
            if (!rejected) throw new Exception("Invalid voice record accepted");
        }
        Console.WriteLine("PASS: voice contract, L2TB round trip and invalid input rejection");
    }
}
