using GC.Web;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

internal static class MapPackageChecks
{
    public static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "gc-map-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new MapPackages(root);
            var fullMap = JsonNode.Parse("""{"name":"Full","worldSuffix":"worlds/test.ent","bounds":[0,0,100,100],"image":"full.webp","details":"details.json"}""")!;
            var rasterMap = JsonNode.Parse("""{"name":"Basic","worldSuffix":"worlds/test.ent","bounds":[0,0,100,100],"image":"basic.webp","quality":"raster"}""")!;
            foreach (var order in new[] {new JsonArray(fullMap.DeepClone(), rasterMap.DeepClone()), new JsonArray(rasterMap.DeepClone(), fullMap.DeepClone())})
            {
                var merged = MapPackages.PreferDetailed(order);
                Check(merged.Count == 1 && (string?)merged[0]!["image"] == "full.webp", "Full map wins regardless of import order");
                Check((string?)merged[0]!["fallbackImage"] == "basic.webp", "Retains calibrated raster fallback");
            }
            var uncalibrated = rasterMap.DeepClone(); uncalibrated["bounds"]![2] = 200;
            Check(MapPackages.PreferDetailed(new JsonArray(fullMap.DeepClone(), uncalibrated))[0]!["fallbackImage"] is null, "Does not attach mismatched bounds");
            Check(MapPackages.IsRaster(MapPackages.PreferDetailed(new JsonArray(rasterMap.DeepClone()))[0]!), "Basic map remains marked incomplete");

            using var valid = Package();
            var first = await store.Import(valid, default);
            Check(store.Active().Count == 1, "Installs a package");
            Check(((string?)first["map"]!["image"])!.StartsWith("/map-packages/test/"), "Resolves immutable asset URLs");
            valid.Position = 0; await store.Import(valid, default);
            Check(store.Active().Count == 1, "Repeated upload is idempotent");
            foreach (var mode in new[] { "traversal", "checksum", "missing", "duplicate", "external", "version", "unlisted", "case", "parts", "externalFallback", "missingFallback", "badAlias", "aliasConflict" })
            {
                using var bad = Package(mode);
                try { await store.Import(bad, default); throw new Exception("Accepted " + mode); }
                catch (InvalidDataException) { Console.WriteLine("PASS: Rejects " + mode); }
                Check(store.Active().ToJsonString() == new JsonArray(first.DeepClone()).ToJsonString() || (string?)store.Active()[0]!["revision"] == (string?)first["revision"], "Failed import preserves active version");
            }
            var real = Environment.GetEnvironmentVariable("GC_TEST_MAP_PACKAGE");
            if (real is not null)
            {
                await using var input = File.OpenRead(real);
                var result = await store.Import(input, default);
                Check(result["map"]!["buildings"] is not null, "Complete reference map and building previews import");
                var library = Path.Combine(root, "previews");
                Check(Directory.GetFiles(library, "*.webp").Length > 0, "Stores shared previews");
                var catalogPath = ((string)result["map"]!["buildings"]!)["/map-packages/".Length..];
                var catalog = JsonNode.Parse(File.ReadAllText(Path.Combine(root, catalogPath)))!;
                Check(catalog["models"]!.AsObject().Any(p => ((string?)p.Value!["image"])?.StartsWith("/map-previews/") == true), "Catalog uses shared preview URLs");
                Check(!Directory.EnumerateFiles(Path.Combine(root, (string)result["id"]!, (string)result["revision"]!), "*.webp", SearchOption.AllDirectories).Any(p => Path.GetDirectoryName(p)!.EndsWith("buildings")), "No per-map preview copies");
                var previewCount = Directory.GetFiles(library).Length;
                using var secondMap = new MemoryStream();
                await using (var source = File.OpenRead(real)) await source.CopyToAsync(secondMap);
                secondMap.Position = 0;
                using (var archive = new ZipArchive(secondMap, ZipArchiveMode.Update, true))
                {
                    var entry = archive.GetEntry("manifest.json")!;
                    JsonNode metadata;
                    using (var reader = new StreamReader(entry.Open())) metadata = JsonNode.Parse(reader.ReadToEnd())!;
                    entry.Delete(); metadata["map"]!.AsObject().Remove("worldAliases"); metadata["id"] = "shared-copy"; metadata["map"]!["worldSuffix"] = "worlds/shared-copy.ent";
                    using var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open());
                    writer.Write(metadata.ToJsonString());
                }
                secondMap.Position = 0; await store.Import(secondMap, default);
                Check(Directory.GetFiles(library).Length == previewCount, "Different maps reuse identical preview files");
                await MapPackageHttpChecks.Run(real);
            }
            Console.WriteLine("PASS: Map package checks complete");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static MemoryStream Package(string mode = "")
    {
        var image = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a2ioAAAAASUVORK5CYII=");
        var manifest = new JsonObject
        {
            ["format"]="gc-map", ["version"]=mode=="version" ? 2 : 1, ["id"]="test",
            ["map"]=new JsonObject { ["name"]="Test", ["worldSuffix"]="worlds/test.ent", ["bounds"]=new JsonArray(0,0,100,100), ["image"]=mode=="external" ? "https://example.org/map.png" : mode=="missing" ? "@map/absent.png" : "@map/map.png" },
            ["files"]=new JsonArray(new JsonObject { ["path"]="map.png", ["bytes"]=image.Length, ["sha256"]=mode=="checksum" ? "bad" : Convert.ToHexString(SHA256.HashData(image)) })
        };
        if (mode == "badAlias") manifest["map"]!["worldAliases"] = new JsonArray("worlds/../other.ent");
        if (mode == "aliasConflict")
        {
            manifest["id"] = "another";
            manifest["map"]!["worldSuffix"] = "worlds/another.ent";
            manifest["map"]!["worldAliases"] = new JsonArray("worlds/test.ent");
        }
        if (mode == "externalFallback") manifest["map"]!["fallbackImage"] = "https://example.org/map.png";
        if (mode == "missingFallback") manifest["map"]!["fallbackImage"] = "@map/absent.png";
        if (mode == "case") manifest["map"]!["image"] = "@map/MAP.png";
        if (mode == "parts") manifest["map"]!["parts"] = new JsonArray("https://example.org/parts.json");
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open())) writer.Write(manifest.ToJsonString());
            using (var file = zip.CreateEntry("map.png").Open()) file.Write(image);
            if (mode is "traversal" or "duplicate" or "unlisted")
                using (var file = zip.CreateEntry(mode=="traversal" ? "../escape.png" : mode=="duplicate" ? "map.png" : "other.png").Open()) file.Write(image);
        }
        stream.Position=0;return stream;
    }
    private static void Check(bool condition, string label) { if (!condition) throw new Exception(label); Console.WriteLine("PASS: " + label); }
}
