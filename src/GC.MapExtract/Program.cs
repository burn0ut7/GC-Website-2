using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GC.MapExtract.Archives;

// Offline prototype: explicit installed source roots, no editor, game process or network.
if (args.Length < 3) throw new ArgumentException("Usage: GC.MapExtract <recipe.json|--list> <private-cache> <pak-root> [pak-root ...]");
var cache = Path.GetFullPath(args[1]);
var entries = new List<(string Archive, PakEntry Entry)>();
foreach (var root in args.Skip(2).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
{
    if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
    foreach (var archive in Directory.EnumerateFiles(root, "*.pak", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
    {
        using var input = File.OpenRead(archive);
        entries.AddRange(PakArchive.Catalogue(input, root, Path.GetRelativePath(root, archive).Replace('\\', '/'), default).Select(e => (archive, e)));
    }
}
if (args[0] == "--list")
{
    Console.WriteLine(JsonSerializer.Serialize(entries.Where(e => e.Entry.Extension == ".topo").Select(e => new { e.Archive, e.Entry.Path })));
    return;
}
// Discover fixture wiring from authored scenario and prefab declarations in the selected addon.
if (args[0] == "--discover-workshop")
{
    string ReadText((string Archive, PakEntry Entry) item)
    {
        using var stream = File.OpenRead(item.Archive);
        return Encoding.UTF8.GetString(PakArchive.Read(stream, item.Entry, default));
    }
    string[] References(string text, string pattern) => Regex.Matches(text, pattern, RegexOptions.Multiline | RegexOptions.IgnoreCase)
        .Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    var fixtures = new HashSet<(string Parent, string Ai, string Map)>();
    foreach (var scene in entries.Where(e => e.Entry.Extension == ".ent"))
    {
        var parents = References(ReadText(scene), @"^\s*SubScene\s*\{\s*Parent\s+""(\{[a-f0-9]{16}\}[^""]+\.ent)""");
        if (parents.Length != 1) continue;
        var layers = scene.Entry.Path[..^4] + "_Layers/";
        var wiring = string.Join("\n", entries.Where(e => e.Entry.Extension == ".layer" && e.Entry.Path.StartsWith(layers, StringComparison.OrdinalIgnoreCase)).Select(ReadText));
        string[] Prefabs(string type) => References(wiring, "^\\s*" + type + @"(?:\s+\w+)?\s*:\s*""(\{[a-f0-9]{16}\}[^""]+\.et)""");
        var ai = Prefabs("SCR_AIWorld");
        var map = Prefabs("SCR_MapEntity");
        if (ai.Length == 1 && map.Length == 1) fixtures.Add((parents[0], ai[0], map[0]));
    }
    if (fixtures.Count != 1) throw new InvalidDataException($"Found {fixtures.Count} distinct scenario setups. Supply an explicit workshop recipe.");
    var fixture = fixtures.Single();
    Console.WriteLine(JsonSerializer.Serialize(new { parentWorld = fixture.Parent, aiWorld = fixture.Ai, mapEntity = fixture.Map }));
    return;
}
if (args[0] == "--terrain")
{
    var prefab = entries.Single(e => e.Entry.Path.Equals(args[1], StringComparison.OrdinalIgnoreCase));
    using var prefabStream = File.OpenRead(prefab.Archive);
    var definition = Encoding.UTF8.GetString(PakArchive.Read(prefabStream, prefab.Entry, default));
    string Resource(string field)
    {
        var matches = Regex.Matches(definition, Regex.Escape("\"" + field + "\"") + @"\s+""\{[A-Fa-f0-9]{16}\}([^""]+)""");
        if (matches.Count != 1) throw new InvalidDataException($"Expected one {field} in {prefab.Entry.Path}.");
        return matches[0].Groups[1].Value;
    }
    var topoPath = Resource("Map Geometry Data");
    var topoEntry = entries.Single(e => e.Entry.Path.Equals(topoPath, StringComparison.OrdinalIgnoreCase));
    var rasterPath = Resource("Satellite background image");
    var rasterEntry = entries.Single(e => e.Entry.Path.Equals(rasterPath, StringComparison.OrdinalIgnoreCase));
    var tiles = entries.Select(e => Regex.Match(e.Entry.Path, @"^(.+?)(\d+)_supertexture\.edds$", RegexOptions.IgnoreCase))
        .Where(match => match.Success).ToList();
    var prefix = tiles.Select(match => match.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).Single();
    using var stream = File.OpenRead(topoEntry.Archive);
    var hash = SHA256.HashData(PakArchive.Read(stream, topoEntry.Entry, default));
    Console.WriteLine(JsonSerializer.Serialize(new { topography = topoEntry.Entry.Path,
        topographySha256 = Convert.ToHexString(hash).ToLowerInvariant(), raster = rasterEntry.Entry.Path,
        tilePrefix = prefix, tileCount = tiles.Count }));
    return;
}
var recipeBytes = File.ReadAllBytes(args[0]);
using var recipe = JsonDocument.Parse(recipeBytes);
var spec = recipe.RootElement;
if (spec.TryGetProperty("mode", out var mode) && mode.GetString() == "raster")
{
    var rasterPath = spec.GetProperty("raster").GetString()!;
    var matches = entries.Where(e => e.Entry.Path.Equals(rasterPath, StringComparison.OrdinalIgnoreCase)).ToList();
    if (matches.Count != 1) throw new InvalidDataException($"Expected one installed raster, found {matches.Count}: {rasterPath}");
    var selectedRaster = matches[0];
    using var input = File.OpenRead(selectedRaster.Archive);
    var bytes = PakArchive.Read(input, selectedRaster.Entry, default);
    Directory.CreateDirectory(cache);
    var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    var output = Path.Combine(cache, hash + ".edds");
    File.WriteAllBytes(output, bytes);
    Console.WriteLine(output);
    return;
}
var topo = spec.GetProperty("topography").GetString()!;
var raster = spec.GetProperty("raster").GetString()!;
var tilePrefix = spec.GetProperty("tilePrefix").GetString()!;
var tileCount = spec.GetProperty("tileCount").GetInt32();
if (tileCount < 1 || tileCount > 16384) throw new InvalidDataException("Unsupported terrain tile count");
var tilePattern = new Regex("^" + Regex.Escape(tilePrefix) + @"(\d+)_supertexture\.edds$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
var selected = entries.Where(e => e.Entry.Path.Equals(topo, StringComparison.OrdinalIgnoreCase) || e.Entry.Path.Equals(raster, StringComparison.OrdinalIgnoreCase) || tilePattern.IsMatch(e.Entry.Path)).OrderBy(e => e.Entry.Path, StringComparer.OrdinalIgnoreCase).ToList();
var duplicates = selected.GroupBy(e => e.Entry.Path, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() != 1).ToList();
if (duplicates.Count != 0) throw new InvalidDataException("Ambiguous archive overrides: supply only the intended source set. " + duplicates[0].Key);
if (!selected.Any(e => e.Entry.Path.Equals(topo, StringComparison.OrdinalIgnoreCase)) || !selected.Any(e => e.Entry.Path.Equals(raster, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Required map texture or topography not installed");
var ids = selected.Select(e => tilePattern.Match(e.Entry.Path)).Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value)).Order().ToArray();
if (!ids.SequenceEqual(Enumerable.Range(0, tileCount))) throw new InvalidDataException("Incomplete or unexpected terrain tile set");
Directory.CreateDirectory(Path.Combine(cache, "objects"));
var sources = new List<object>();
using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
fingerprint.AppendData(recipeBytes);
foreach (var (archive, entry) in selected)
{
    using var stream = File.OpenRead(archive);
    var bytes = PakArchive.Read(stream, entry, default);
    var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    fingerprint.AppendData(Encoding.UTF8.GetBytes(entry.Path.ToLowerInvariant() + "\n" + hash + "\n"));
    var file = Path.Combine(cache, "objects", hash);
    if (File.Exists(file))
    {
        if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant() != hash) throw new InvalidDataException("Corrupt cached original: " + hash);
    }
    else
    {
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, file);
    }
    sources.Add(new { path = entry.Path, sha256 = hash, file, archive, entry.Compression, entry.OriginalBytes });
}
var key = Convert.ToHexString(fingerprint.GetHashAndReset()).ToLowerInvariant();
var result = Path.Combine(cache, key + ".json");
File.WriteAllText(result, JsonSerializer.Serialize(new { version = 1, fingerprint = key, recipe = spec, sources }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(result);
