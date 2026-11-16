using System.Text.Json;
using System.Text.RegularExpressions;

namespace GC.Infrastructure;

/// <summary>Validates schema-2 authoring evidence without certifying complete runtime dependencies.</summary>
internal static class MissionDependencyEvidence
{
    public static void Validate(JsonElement root)
    {
        static JsonElement Value(JsonElement source, string key) => source.ValueKind == JsonValueKind.Object && source.TryGetProperty(key, out var value)
            ? value : throw new InvalidDataException("Missing dependency evidence " + key);
        static string Text(JsonElement source, string key, int limit)
        {
            var value = Value(source, key);
            if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 } text || text.Length > limit || text.Any(char.IsControl))
                throw new InvalidDataException("Invalid dependency evidence " + key);
            return text;
        }
        static JsonElement Array(JsonElement source, string key, int limit)
        {
            var value = Value(source, key);
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > limit) throw new InvalidDataException("Invalid dependency evidence " + key);
            return value;
        }
        static bool Id(string id) => Regex.IsMatch(id, "^[A-Za-z0-9_.-]{1,128}$");
        if (Text(root, "resourceCoverage", 80) != "authored_container_graph_partial") throw new InvalidDataException("Unsupported dependency coverage");
        foreach (var key in new[] { "unexpandedResourceCount", "omittedVanillaResourceCount" })
        {
            var count = Value(root, key);
            if (count.ValueKind != JsonValueKind.Number || !count.TryGetInt32(out var n) || n is < 0 or > 50000) throw new InvalidDataException("Invalid dependency count");
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var guids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var addon in Array(root, "loadedAddons", 512).EnumerateArray())
        {
            var id = Text(addon, "id", 128); var guid = Text(addon, "guid", 16);
            if (!Id(id) || !Regex.IsMatch(guid, "^[0-9A-Fa-f]{16}$") || !ids.Add(id) || !guids.Add(guid))
                throw new InvalidDataException("Ambiguous or invalid addon identity");
            foreach (var flag in new[] { "vanilla", "referencedByPlacedPrefab", "referencedByResource" })
                if (Value(addon, flag).ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException("Invalid addon flag");
        }
        void CheckIds(JsonElement values)
        {
            foreach (var value in values.EnumerateArray())
                if (value.ValueKind != JsonValueKind.String || !ids.Contains(value.GetString()!)) throw new InvalidDataException("Referenced addon has no GUID mapping");
        }
        CheckIds(Array(root, "referencedAddonIds", 256));
        var resources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in Array(root, "resourceReferences", 50000).EnumerateArray())
        {
            var resource = Text(row, "resource", 1024);
            if (!resources.Add(resource)) throw new InvalidDataException("Duplicate resource evidence");
            _ = Text(row, "origin", 16384);
            if (Text(row, "status", 40) is not ("container" or "unresolved")) throw new InvalidDataException("Invalid resource status");
            CheckIds(Array(row, "addonIds", 512));
        }
    }
}
