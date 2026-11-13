using Npgsql;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Buffers.Binary;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.Features;

namespace GC.Web;

// Immutable asset directories plus one atomic registry replacement. No game runtime on the server.
public sealed class MapPackages(string root)
{
    private string rootPath => root;
    public const long UploadLimit = 256L * 1024 * 1024;
    private readonly SemaphoreSlim gate = new(1);
    private static readonly Regex Id = new("^[a-z0-9][a-z0-9-]{0,63}$");
    private static readonly Regex Asset = new("^[a-zA-Z0-9_-]+(?:/[a-zA-Z0-9_-]+)*\\.(json|webp|png)$");
    private static readonly System.Text.Json.JsonSerializerOptions Json = new() { WriteIndented = true };
    private string Registry => Path.Combine(root, "active.json");
    public JsonArray Active() => File.Exists(Registry) ? JsonNode.Parse(File.ReadAllText(Registry))!.AsArray() : [];

    public async Task<JsonObject> Import(Stream input, CancellationToken cancellation)
    {
        Directory.CreateDirectory(root);
        var temporary = Path.Combine(root, ".upload-" + Guid.NewGuid().ToString("N"));
        var stage = temporary + "-files";
        try
        {
            await using (var output = File.Create(temporary))
            {
                byte[] buffer = new byte[65536]; long total = 0; int count;
                while ((count = await input.ReadAsync(buffer, cancellation)) > 0)
                {
                    total += count;
                    if (total > UploadLimit) throw new InvalidDataException("Package exceeds 256 MB.");
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellation);
                }
            }
            string digest;
            await using (var file = File.OpenRead(temporary))
                digest = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellation)).ToLowerInvariant();
            using var zip = ZipFile.OpenRead(temporary);
            var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
            var portableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long expanded = 0;
            foreach (var entry in zip.Entries)
            {
                if (entries.Count >= 10000 || !portableNames.Add(entry.FullName) || !entries.TryAdd(entry.FullName, entry) ||
                    (entry.FullName != "manifest.json" && !Asset.IsMatch(entry.FullName)) ||
                    entry.Length > 64L * 1024 * 1024 || (expanded += entry.Length) > 512L * 1024 * 1024)
                    throw new InvalidDataException("Invalid archive paths, duplicate entries or package size.");
            }
            if (!entries.TryGetValue("manifest.json", out var manifestEntry) || manifestEntry.Length > 2 * 1024 * 1024)
                throw new InvalidDataException("Missing or oversized manifest.");
            using var manifestStream = manifestEntry.Open();
            var manifest = (await JsonNode.ParseAsync(manifestStream, cancellationToken: cancellation))!.AsObject();
            if ((string?)manifest["format"] != "gc-map" || (int?)manifest["version"] != 1)
                throw new InvalidDataException("Unsupported map package version.");
            var id = (string?)manifest["id"] ?? "";
            if (!Id.IsMatch(id)) throw new InvalidDataException("Invalid map identifier.");
            var map = manifest["map"]!.DeepClone().AsObject();
            if (map.ContainsKey("fallbackImage")) throw new InvalidDataException("fallbackImage is derived by the server and cannot be imported.");
            var bounds = map["bounds"]!.AsArray().Select(n => n!.GetValue<double>()).ToArray();
            var suffix = (string?)map["worldSuffix"] ?? "";
            if (bounds.Length != 4 || bounds.Any(n => !double.IsFinite(n)) || bounds[2] <= bounds[0] || bounds[3] <= bounds[1] ||
                string.IsNullOrWhiteSpace((string?)map["name"]) || !suffix.EndsWith(".ent", StringComparison.OrdinalIgnoreCase) ||
                suffix.Contains("..") || suffix.Contains('\\') || suffix.StartsWith('/') || suffix.Length > 512)
                throw new InvalidDataException("Invalid map name, world or calibration.");
            var worlds = WorldSuffixes(map).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var files = manifest["files"]!.AsArray();
            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in files)
            {
                var path = (string?)file!["path"] ?? "";
                if (!Asset.IsMatch(path) || path == "manifest.json" || !paths.Add(path) || !entries.ContainsKey(path))
                    throw new InvalidDataException("Invalid asset inventory.");
            }
            if (paths.Count + 1 != entries.Count) throw new InvalidDataException("Unlisted archive assets.");
            // Identical preview bytes have one authenticated URL across all maps.
            var previews = files.Where(f => Regex.IsMatch((string?)f!["path"] ?? "", "^maps/buildings/[a-f0-9]{16}\\.webp$"))
                .ToDictionary(f => (string)f!["path"]!, f => ((string?)f!["sha256"] ?? "").ToLowerInvariant());
            if (previews.Values.Any(hash => !Regex.IsMatch(hash, "^[a-f0-9]{64}$")))
                throw new InvalidDataException("Invalid preview checksum.");
            digest = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("shared-previews-v1:" + digest))).ToLowerInvariant();
            var prefix = $"/map-packages/{id}/{digest}/";
            JsonNode Rewrite(JsonNode node)
            {
                if (node is JsonObject obj)
                {
                    foreach (var key in obj.Select(k => k.Key).ToArray()) if (obj[key] is { } child)
                    {
                        if (key == "parts" && child is JsonArray parts && parts.Any(p => p is null || !p.GetValue<string>().StartsWith("@map/")))
                            throw new InvalidDataException("Terrain parts must be package-local.");
                        if (key is "image" or "url" && child is JsonValue url && url.TryGetValue<string>(out var address) && !address.StartsWith("@map/"))
                            throw new InvalidDataException("Image references must be package-local.");
                        var rewritten = Rewrite(child);
                        if (!ReferenceEquals(child, rewritten)) obj[key] = rewritten;
                    }
                }
                else if (node is JsonArray array)
                {
                    for (var i = 0; i < array.Count; i++) if (array[i] is { } child)
                    {
                        var rewritten = Rewrite(child);
                        if (!ReferenceEquals(child, rewritten)) array[i] = rewritten;
                    }
                }
                else if (node is JsonValue value && value.TryGetValue<string>(out var text) && text.StartsWith("@map/"))
                {
                    var path = text[5..];
                    if (!paths.Contains(path)) throw new InvalidDataException("An asset reference is missing from the package.");
                    return JsonValue.Create(previews.TryGetValue(path, out var hash) ? $"/map-previews/{hash}.webp" : prefix + path)!;
                }
                return node;
            }
            foreach (var field in new[] { "image", "details", "labels", "symbols", "buildings" })
                if (map[field] is { } value && !value.GetValue<string>().StartsWith("@map/")) throw new InvalidDataException("Map assets must be package-local.");
            if (map["image"] is null) throw new InvalidDataException("Missing map image.");
            if (map["natural"] is JsonObject natural)
                foreach (var pair in natural) if (pair.Value is null || !pair.Value.GetValue<string>().StartsWith("@map/"))
                    throw new InvalidDataException("Natural layers must be package-local.");
            Rewrite(map);
            Directory.CreateDirectory(stage);
            foreach (var file in files)
            {
                cancellation.ThrowIfCancellationRequested();
                var path = file!["path"]!.GetValue<string>();
                var entry = entries[path];
                if (entry.Length != file["bytes"]!.GetValue<long>()) throw new InvalidDataException("Asset length mismatch.");
                using var bytes = new MemoryStream();
                await using (var source = entry.Open())
                {
                    byte[] buffer = new byte[65536]; int count;
                    while ((count = await source.ReadAsync(buffer, cancellation)) > 0)
                    {
                        if (bytes.Length + count > entry.Length) throw new InvalidDataException("Expanded asset exceeds declared length.");
                        bytes.Write(buffer, 0, count);
                    }
                }
                var data = bytes.ToArray();
                if (!string.Equals(Convert.ToHexString(SHA256.HashData(data)), (string?)file["sha256"], StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Asset checksum mismatch.");
                var destination = Path.Combine(stage, path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (path.EndsWith(".json"))
                {
                    var node = JsonNode.Parse(data) ?? throw new InvalidDataException("Empty JSON asset.");
                    await File.WriteAllTextAsync(destination, Rewrite(node).ToJsonString(), cancellation);
                }
                else
                {
                    var webp = data.Length >= 12 && data.AsSpan(0,4).SequenceEqual("RIFF"u8) && data.AsSpan(8,4).SequenceEqual("WEBP"u8);
                    var png = data.Length >= 8 && data.AsSpan(0,8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10});
                    if (!(path.EndsWith(".webp") ? webp : png)) throw new InvalidDataException("Invalid image format.");
                    ValidateImageSize(data, png);
                    await File.WriteAllBytesAsync(destination, data, cancellation);
                }
            }
            await File.WriteAllTextAsync(Path.Combine(stage, "manifest.json"), manifest.ToJsonString(Json), cancellation);
            await gate.WaitAsync(cancellation);
            try
            {
                var active = Active();
                if (active.Any(n => (string?)n!["id"] == id && !IsRaster(n["map"]!) && IsRaster(map)))
                    throw new InvalidDataException("A basic texture cannot replace a detailed map. Use a separate fallback package ID.");
                if (active.Any(n => (string?)n!["id"] != id && IsRaster(n["map"]!) == IsRaster(map) && WorldSuffixes(n["map"]!).Any(worlds.Contains)))
                    throw new InvalidDataException("This world is already owned by a different map package.");
                var library = Path.Combine(root, "previews");
                Directory.CreateDirectory(library);
                foreach (var preview in previews)
                {
                    var source = Path.Combine(stage, preview.Key.Replace('/', Path.DirectorySeparatorChar));
                    var destination = Path.Combine(library, preview.Value + ".webp");
                    if (!File.Exists(destination)) File.Move(source, destination);
                    else File.Delete(source);
                }
                var final = Path.Combine(root, id, digest);
                Directory.CreateDirectory(Path.GetDirectoryName(final)!);
                if (!Directory.Exists(final)) Directory.Move(stage, final);
                File.Copy(temporary, Path.Combine(final, "package.zip"), true);
                for (var i = active.Count - 1; i >= 0; i--) if ((string?)active[i]!["id"] == id) active.RemoveAt(i);
                var record = new JsonObject { ["id"] = id, ["revision"] = digest, ["installedAt"] = DateTimeOffset.UtcNow, ["map"] = map, ["canExport"] = true };
                active.Add(record);
                await File.WriteAllTextAsync(Registry + ".tmp", active.ToJsonString(Json), cancellation);
                File.Move(Registry + ".tmp", Registry, true);
                return record.DeepClone().AsObject();
            }
            finally { gate.Release(); }
        }
        finally
        {
            File.Delete(temporary);
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
        }
    }

    public static MapPackages FromConfiguration(IConfiguration configuration) => new(Path.GetFullPath(configuration["Maps:StoragePath"] ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GCWebsite", "Maps")));

    public JsonArray AvailableMaps(string webRoot)
        {
            var maps = JsonNode.Parse(File.ReadAllText(Path.Combine(webRoot, "playback-maps.json")))!.AsArray();
            foreach (var record in Active())
            {
                var map = record!["map"]!;
                foreach (var world in WorldSuffixes(map))
                {
                    var entry = map.DeepClone().AsObject();
                    entry.Remove("worldAliases");
                    entry["worldSuffix"] = world;
                    maps.Add(entry);
                }
            }
            maps = PreferDetailed(maps);
            return maps;
        }

    public static void Map(WebApplication app)
    {
        var store = FromConfiguration(app.Configuration);
        var root = store.rootPath;
        JsonArray AvailableMaps() => store.AvailableMaps(app.Environment.WebRootPath);
        app.MapGet("/api/playback/maps", () => Results.Json(AvailableMaps())).RequireAuthorization(SiteNavigation.AdminPolicy);
        // Public catalogue needs map identities, never protected replay assets.
        app.MapGet("/api/catalog/maps", () => Results.Json(AvailableMaps().Select(map => new {
            name = (string?)map!["name"], worldSuffix = (string?)map["worldSuffix"]
        })));
        app.MapGet("/map-previews/{file}", (string file) =>
        {
            if (!Regex.IsMatch(file, "^[a-f0-9]{64}\\.webp$")) return Results.NotFound();
            var path = Path.Combine(root, "previews", file);
            return File.Exists(path) ? Results.File(path, "image/webp") : Results.NotFound();
        }).RequireAuthorization(SiteNavigation.AdminPolicy);
        app.MapGet("/api/admin/maps/missing", async Task<IResult> (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var database = context.RequestServices.GetService<NpgsqlDataSource>();
            if (database is null) return Results.StatusCode(503);
            var maps = AvailableMaps();
            await using var query = database.CreateCommand("""
                SELECT coalesce(header::json->>'world',''), count(DISTINCT (server_id,recording_id))
                FROM gc.telemetry_batches GROUP BY coalesce(header::json->>'world','') ORDER BY 1
                """);
            query.CommandTimeout = 10;
            var missing = new List<object>();
            await using var reader = await query.ExecuteReaderAsync(context.RequestAborted);
            while (await reader.ReadAsync(context.RequestAborted))
            {
                var world = reader.GetString(0);
                var match = maps.FirstOrDefault(map => MatchesWorld(world, (string?)map!["worldSuffix"]));
                if (match is null || IsRaster(match))
                    missing.Add(new { world, recordingCount = reader.GetInt64(1), status = match is null ? "missing" : "raster" });
            }
            return Results.Ok(missing);
        }).RequireAuthorization(SiteNavigation.AdminPolicy);
        app.MapGet("/api/admin/maps", () => Results.Json(store.Active())).RequireAuthorization(SiteNavigation.AdminPolicy);
        app.MapGet("/api/admin/maps/{id}/export", (string id) =>
        {
            if (!Id.IsMatch(id)) return Results.NotFound();
            var item = store.Active().FirstOrDefault(n => (string?)n!["id"] == id);
            if (item is null) return Results.NotFound();
            var path = Path.Combine(root, id, (string)item["revision"]!, "package.zip");
            return File.Exists(path) ? Results.File(path, "application/zip", id + ".zip") : Results.NotFound();
        }).RequireAuthorization(SiteNavigation.AdminPolicy);
        app.MapPost("/api/admin/maps", async (HttpContext context, IAntiforgery csrf, ILogger<MapPackages> logger) =>
        {
            try { await csrf.ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { return Results.BadRequest(new { error = "Please reload the page and try again." }); }
            var limit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = UploadLimit;
            if (context.Request.ContentLength > UploadLimit) return Results.StatusCode(413);
            try
            {
                var record = await store.Import(context.Request.Body, context.RequestAborted);
                logger.LogInformation("Map package {Map} {Revision} imported by {User}", (string?)record["id"], (string?)record["revision"], context.User.Identity?.Name);
                return Results.Json(record);
            }
            catch (Exception error) when (error is InvalidDataException or System.Text.Json.JsonException or InvalidOperationException or FormatException or NullReferenceException or ArgumentException or OverflowException)
            { return Results.BadRequest(new { error = error is InvalidDataException ? error.Message : "Invalid map manifest or asset data." }); }
        }).RequireAuthorization(SiteNavigation.AdminPolicy).RequireRateLimiting("intake");
        app.MapGet("/map-packages/{id}/{revision}/{**asset}", (string id, string revision, string asset) =>
        {
            if (!Id.IsMatch(id) || !Regex.IsMatch(revision, "^[a-f0-9]{64}$") || !Asset.IsMatch(asset) || asset == "manifest.json") return Results.NotFound();
            var path = Path.Combine(root, id, revision, asset.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(path) ? Results.File(path, asset.EndsWith(".json") ? "application/json" : asset.EndsWith(".webp") ? "image/webp" : "image/png") : Results.NotFound();
        }).RequireAuthorization(SiteNavigation.AdminPolicy);
    }

    private static IEnumerable<string> WorldSuffixes(JsonNode map)
    {
        yield return (string)map["worldSuffix"]!;
        if (map["worldAliases"] is null) yield break;
        if (map["worldAliases"] is not JsonArray aliases || aliases.Count > 4096)
            throw new InvalidDataException("Invalid map world aliases.");
        foreach (var alias in aliases)
        {
            var world = (string?)alias ?? "";
            if (!world.StartsWith("worlds/", StringComparison.OrdinalIgnoreCase) || !world.EndsWith(".ent", StringComparison.OrdinalIgnoreCase)
                || world.Length > 512 || world.Contains("..") || world.Contains('\\') || world.Contains(':') || world.Any(char.IsControl))
                throw new InvalidDataException("Invalid map world alias.");
            yield return world;
        }
    }

    public static bool IsRaster(JsonNode map) => (string?)map["quality"] == "raster" || map["details"] is null;

    public static JsonArray PreferDetailed(JsonArray maps)
    {
        var result = new JsonArray();
        foreach (var group in maps.GroupBy(m => ((string)m!["worldSuffix"]!).Replace('\\', '/').ToLowerInvariant()))
        {
            var full = group.LastOrDefault(m => !IsRaster(m!));
            var raster = group.LastOrDefault(m => IsRaster(m!));
            var chosen = (full ?? raster)!.DeepClone();
            if (full is not null && raster is not null && full["bounds"]!.ToJsonString() == raster["bounds"]!.ToJsonString())
                chosen["fallbackImage"] = raster["image"]!.DeepClone();
            result.Add(chosen);
        }
        return result;
    }

    public static bool MatchesWorld(string? world, string? suffix) =>
        !string.IsNullOrWhiteSpace(world) && !string.IsNullOrWhiteSpace(suffix) &&
        world.Replace('\\', '/').EndsWith(suffix.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static void ValidateImageSize(byte[] data, bool png)
    {
        int width = 0, height = 0;
        if (png && data.Length >= 24 && data.AsSpan(12,4).SequenceEqual("IHDR"u8))
        {
            width = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16,4));
            height = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(20,4));
        }
        else if (!png && data.Length >= 30)
        {
            var kind = System.Text.Encoding.ASCII.GetString(data,12,4);
            if (kind == "VP8X")
            {
                width = 1 + data[24] + (data[25]<<8) + (data[26]<<16);
                height = 1 + data[27] + (data[28]<<8) + (data[29]<<16);
            }
            else if (kind == "VP8L" && data[20] == 0x2f)
            {
                var bits = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(21,4));
                width = 1 + (int)(bits & 0x3fff); height = 1 + (int)((bits>>14) & 0x3fff);
            }
            else if (kind == "VP8 " && data[23] == 0x9d && data[24] == 1 && data[25] == 0x2a)
            {
                width = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(26,2)) & 0x3fff;
                height = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(28,2)) & 0x3fff;
            }
        }
        if (width < 1 || height < 1 || width > 8192 || height > 8192)
            throw new InvalidDataException("Unsupported image header or dimensions (maximum 8192 pixels per side).");
    }
}
