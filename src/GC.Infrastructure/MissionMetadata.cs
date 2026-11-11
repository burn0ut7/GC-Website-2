using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace GC.Infrastructure;

/// <summary>One normalized authoring contract for Lobby2 headers, legacy GC headers and JSON manifests.</summary>
public static class MissionMetadata
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static JsonObject FromHeader(MissionConfig header)
    {
        var result = new JsonObject();
        void Text(string target, MissionConfig source, string key) { var value = source.Text(key); if (value.Length > 0) result[target] = value; }
        void Array(string target, MissionConfig source, string key) { if (source.Object(key) is not null) result[target] = JsonSerializer.SerializeToNode(source.Array(key)); }
        var legacy = header.Object("m_GC_Metadata");
        if (legacy is not null)
        {
            var era = legacy.Text("m_eEra");
            if (era.StartsWith("ERA_") && era != "ERA_OTHER") result["era"] = era[4..];
            var assets = legacy.Array("m_eSpecialAssets").Where(x => x != "UNDEFINED").Select(x => x switch {
                "TANKS" => "Tanks", "ATK_HELIS" => "Attack helicopters", "MORTARS" => "Mortars", "ARTILLERY" => "Artillery", "DRONES" => "Drones", "BOATS" => "Boats", _ => x });
            result["assetTypes"] = JsonSerializer.SerializeToNode(assets);
            var forces = new JsonArray();
            foreach (var (field, side) in new[] { ("m_FriendlyForce", "friendly"), ("m_OpposingForce", "opposing") })
                if (legacy.Object(field) is { } force && force.Text("m_sFactionKey") is { Length: > 0 } faction)
                    forces.Add(new JsonObject { ["faction"] = faction, ["forceType"] = force.Text("m_eType"), ["side"] = side });
            result["forces"] = forces;
            if (legacy.Object("m_MissionGroup") is { } group && group.Text("m_sGroupID") is { Length: > 0 } id)
            {
                var entry = new JsonObject { ["id"] = id };
                if (group.Text("m_fGroupSimilarity") is { Length: > 0 } similarity) entry["similarity"] = Number(similarity);
                result["missionGroup"] = entry;
            }
        }
        Array("tags", header, "m_aManualTags");
        // Explicit Lobby2 values override legacy values field-by-field; omissions preserve them.
        if (header.Object("m_GC_Catalog") is { } authored)
        {
            if (authored.Text("m_iSchemaVersion") is { Length: > 0 } version && version != "1") throw new InvalidDataException("Unsupported catalogue schema");
            foreach (var (target, key) in new[] { ("missionType", "m_sMissionType"), ("era", "m_sEra"), ("map", "m_sMapName") }) Text(target, authored, key);
            // Workbench string combo boxes serialize the chosen label, not ParamEnum's numeric value.
            if (result["missionType"] is { } selectedType)
                result["missionType"] = selectedType.GetValue<string>() switch {
                    "Not set" => null, "CO-OP - Cooperative" => "COOP", "TVT - Team versus team" => "TVT",
                    "COTVT - Cooperative and team versus team" => "COTVT", "Training" => "TRNG",
                    "LOL - Non-serious mission" => "LOL", "Testing" => "TEST", "Broken" => "BROKEN",
                    var code => code
                };
            foreach (var (target, key) in new[] { ("objectives", "m_aObjectives"), ("assetTypes", "m_aPlayerAssets"), ("mods", "m_aMods"), ("requiredAddonIds", "m_aRequiredAddonIds") }) Array(target, authored, key);
            foreach (var key in new[] { "objectives", "assetTypes" })
                if (result[key] is JsonArray choices)
                    result[key] = JsonSerializer.SerializeToNode(choices.Select(c => c!.GetValue<string>()).Where(s => s.Length > 0 && s != "Not set").Select(s => s == "High-value target (HVT)" ? "HVT" : s == "Cars / light vehicles" ? "Cars" : s));
            var minimum = authored.Text("m_iMinPlayers");
            if (minimum.Length > 0 && minimum != "0")
                result["minPlayers"] = int.TryParse(minimum, out var n) ? n : throw new InvalidDataException("Invalid minimum player count");
            // Retired GC authoring fields may remain serialized. Native Player Count and
            // exported world factions/slots are authoritative; stale hidden values are ignored.
            if (authored.Text("m_sGroupId") is { Length: > 0 } groupId)
            {
                var group = new JsonObject { ["id"] = groupId };
                if (authored.Text("m_fGroupSimilarity") is { Length: > 0 } similarity && similarity != "-1") group["similarity"] = Number(similarity);
                result["missionGroup"] = group;
            }
        }
        Text("details", header, "m_sDetails");
        Text("previewResource", header, "m_sPreviewImage");
        return result;
    }

    private static double Number(string value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : throw new InvalidDataException("Invalid metadata number");

    public static JsonObject Normalize(JsonElement root, int? maxPlayers, string? fallbackType = null)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Metadata must be an object");
        if (root.TryGetProperty("schemaVersion", out var schema) && (schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) || version != 1)) throw new InvalidDataException("Unsupported catalogue schema");
        var categories = MissionCategories.Parse(root, fallbackType);
        var result = JsonSerializer.SerializeToNode(categories, Json)!.AsObject();
        string? Text(JsonElement obj, string key, int limit = 80)
        {
            if (!obj.TryGetProperty(key, out var v) || v.ValueKind == JsonValueKind.Null) return null;
            if (v.ValueKind != JsonValueKind.String) throw new InvalidDataException("Invalid " + key);
            var s = v.GetString()!.Trim();
            if (s.Length > limit || s.Any(c => char.IsControl(c) && !(key == "details" && c is '\n' or '\r' or '\t'))) throw new InvalidDataException("Invalid " + key);
            return s.Length == 0 ? null : s;
        }
        int? Count(JsonElement obj, string key)
        {
            if (!obj.TryGetProperty(key, out var v) || v.ValueKind == JsonValueKind.Null) return null;
            if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var n) || n is <= 0 or > 10000) throw new InvalidDataException("Invalid " + key);
            return n;
        }
        var min = Count(root, "minPlayers"); var recommended = Count(root, "recommendedPlayers");
        if (maxPlayers is <= 0 or > 10000 || min > maxPlayers || recommended > maxPlayers || recommended < min) throw new InvalidDataException("Invalid player range");
        result["schemaVersion"] = 1;
        result["minPlayers"] = min; result["recommendedPlayers"] = recommended;
        result["era"] = Text(root, "era"); result["map"] = Text(root, "map");
        result["details"] = Text(root, "details", 20000); result["previewResource"] = Text(root, "previewResource", 512);
        var tags = new JsonArray();
        if (root.TryGetProperty("tags", out var tagValues))
        {
            if (tagValues.ValueKind != JsonValueKind.Array || tagValues.GetArrayLength() > 32) throw new InvalidDataException("Invalid tags");
            foreach (var value in tagValues.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.String || value.GetString()!.Trim() is not { Length: > 0 and <= 80 } tag || tag.Any(char.IsControl)) throw new InvalidDataException("Invalid tag");
                tags.Add(tag);
            }
        }
        result["tags"] = tags;
        var dependencies = new JsonArray();
        if (root.TryGetProperty("requiredAddonIds", out var ids))
        {
            if (ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() > 128) throw new InvalidDataException("Invalid required addon IDs");
            foreach (var id in ids.EnumerateArray())
            {
                if (id.ValueKind != JsonValueKind.String || !Regex.IsMatch(id.GetString()!, "^[0-9A-Fa-f]{16}$")) throw new InvalidDataException("Invalid required addon ID");
                dependencies.Add(id.GetString()!.ToUpperInvariant());
            }
        }
        result["requiredAddonIds"] = dependencies;
        var forces = new JsonArray();
        if (root.TryGetProperty("forces", out var entries))
        {
            if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > 32) throw new InvalidDataException("Invalid forces");
            foreach (var f in entries.EnumerateArray())
            {
                if (f.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid force");
                var faction = Text(f, "faction") ?? throw new InvalidDataException("Force requires faction");
                bool? playable = null;
                if (f.TryGetProperty("playable", out var p) && p.ValueKind != JsonValueKind.Null) {
                    if (p.ValueKind is not JsonValueKind.True and not JsonValueKind.False) throw new InvalidDataException("Invalid playable flag");
                    playable = p.GetBoolean();
                }
                forces.Add(new JsonObject { ["faction"] = faction, ["forceType"] = Text(f, "forceType"), ["side"] = Text(f, "side"), ["playable"] = playable, ["slots"] = Count(f, "slots") });
            }
        }
        result["forces"] = forces;
        if (root.TryGetProperty("missionGroup", out var group) && group.ValueKind != JsonValueKind.Null)
        {
            if (group.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid mission group");
            var value = new JsonObject { ["id"] = Text(group, "id") ?? throw new InvalidDataException("Missing mission group ID") };
            if (group.TryGetProperty("similarity", out var similarity) && similarity.ValueKind != JsonValueKind.Null) {
                if (similarity.ValueKind != JsonValueKind.Number || !similarity.TryGetDouble(out var n) || !double.IsFinite(n) || n < 0 || n > 1) throw new InvalidDataException("Invalid mission similarity");
                value["similarity"] = n;
            }
            result["missionGroup"] = value;
        }
        return result;
    }

    public static string[] SearchTags(JsonObject metadata)
    {
        using var doc = JsonDocument.Parse(metadata.ToJsonString());
        var tags = metadata["tags"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
        if (metadata["era"] is { } era) tags.Add(era.GetValue<string>());
        if (metadata["startTime"] is { } startTime) tags.Add(startTime.GetValue<string>());
        foreach (var f in metadata["forces"]!.AsArray()) foreach (var key in new[] { "faction", "forceType" })
            if (f![key] is { } value) tags.Add(value.GetValue<string>());
        return MissionCategories.Parse(doc.RootElement).SearchTags(tags);
    }

    // Native config defaults match GC_GeneratedMissionMetadata's attributed fields.
    // JSON here is the website's normalized record, never an addon-side file.
    internal static JsonObject? GeneratedFromHeader(MissionConfig header)
    {
        if (header.Find("m_GC_GeneratedMetadata") is not { } field) return null;
        if (field.Body is not { } source || field.Values.Length is < 2 or > 3 || field.Values[1] != "GC_GeneratedMissionMetadata")
            throw new InvalidDataException("Unsupported generated metadata object");
        static int Integer(MissionConfig source, string key, int fallback = 0) => source.Text(key) is { Length: > 0 } value
            ? int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : throw new InvalidDataException("Invalid " + key) : fallback;
        static bool Flag(MissionConfig source, string key) => source.Text(key) switch {
            "" or "0" => false, "1" => true, _ => throw new InvalidDataException("Invalid " + key)
        };
        static IEnumerable<MissionConfig> Rows(MissionConfig source, string key, string type)
        {
            foreach (var entry in source.Object(key)?.Entries ?? [])
            {
                if (entry.Body is null || entry.Values.Length is < 1 or > 2 || entry.Values[0] != type)
                    throw new InvalidDataException("Invalid " + key + " entry");
                yield return entry.Body;
            }
        }
        var result = new JsonObject {
            ["schemaVersion"] = Integer(source, "m_iSchemaVersion", 1),
            ["world"] = source.Text("m_sWorld"), ["scope"] = source.Text("m_sCaptureScope"),
            ["entityCount"] = Integer(source, "m_iEntityCount")
        };
        foreach (var (target, key) in new[] { ("limitations", "m_aLimitations"), ("factions", "m_aFactions"),
            ("configuredFactions", "m_aConfiguredFactions"), ("referencedAddonIds", "m_aReferencedAddonIds") })
            result[target] = JsonSerializer.SerializeToNode(source.Array(key));
        var assets = new JsonObject();
        foreach (var row in Rows(source, "m_aAssets", "GC_MissionMetadataAsset"))
        {
            var category = row.Text("m_sCategory");
            if (category.Length == 0 || assets.ContainsKey(category)) throw new InvalidDataException("Invalid asset category");
            assets[category] = Integer(row, "m_iCount");
        }
        result["assets"] = assets;
        var slots = new JsonArray();
        foreach (var row in Rows(source, "m_aPlayerSlots", "GC_MissionMetadataSlot"))
            slots.Add(new JsonObject {
                ["group"] = row.Text("m_sGroup"), ["index"] = Integer(row, "m_iIndex"),
                ["faction"] = row.Text("m_sFaction"), ["prefab"] = row.Text("m_sPrefab"),
                ["name"] = row.Text("m_sName"), ["loadoutFaction"] = row.Text("m_sLoadoutFaction")
            });
        result["playerSlots"] = slots;
        var addons = new JsonArray();
        foreach (var row in Rows(source, "m_aLoadedAddons", "GC_MissionMetadataAddon"))
            addons.Add(new JsonObject {
                ["id"] = row.Text("m_sId"), ["guid"] = row.Text("m_sGuid"), ["title"] = row.Text("m_sTitle"),
                ["vanilla"] = Flag(row, "m_bVanilla"), ["referencedByPlacedPrefab"] = Flag(row, "m_bReferencedByPlacedPrefab")
            });
        result["loadedAddons"] = addons;
        var hours = source.Array("m_aStartHours");
        if (hours.Length > 0) result["startHours"] = JsonSerializer.SerializeToNode(hours.Select(h =>
            int.TryParse(h, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : throw new InvalidDataException("Invalid start hour")));
        if (Flag(source, "m_bRandomStartTime")) result["randomStartTime"] = true;
        result["allowHeaderTime"] = Flag(source, "m_bAllowHeaderTime");
        return result;
    }

    internal static void ApplyGenerated(JsonObject metadata, string json, string world, MissionConfig? header = null)
    {
        var snapshot = Generated(json, world);
        if (snapshot["startHours"] is not null || snapshot["randomStartTime"] is not null)
        {
            if (header is not null && snapshot["allowHeaderTime"]?.GetValue<bool>() == true && header.Text("m_bOverrideScenarioTimeAndWeather") == "1")
            {
                var selected = header.Text("m_iStartingHours");
                if (header.Text("m_bRandomStartingDaytime") == "1")
                {
                    snapshot.Remove("startHours");
                    snapshot["randomStartTime"] = true;
                }
                else if (int.TryParse(selected.Length == 0 ? "8" : selected, out var hour) && hour is >= 0 and < 24)
                {
                    snapshot.Remove("randomStartTime");
                    snapshot["startHours"] = new JsonArray(JsonValue.Create(hour));
                }
                else throw new InvalidDataException("Invalid scenario start hour");
            }
            var periods = snapshot["startHours"]?.AsArray().Select(h => h!.GetValue<int>() switch {
                >= 6 and < 12 => "Morning", >= 12 and < 17 => "Midday", >= 17 and < 21 => "Evening", _ => "Night"
            }).Distinct().ToArray() ?? [];
            metadata["startTime"] = periods.Length == 1 ? periods[0] : "Variable";
        }
        metadata["generated"] = snapshot;
        if (snapshot["loadedAddons"] is not JsonArray addons) return;
        var mods = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var addon in addons)
        {
            if (addon is not JsonObject row || row["referencedByPlacedPrefab"] is not JsonValue used ||
                !used.TryGetValue<bool>(out var referenced) || !referenced ||
                row["vanilla"] is not JsonValue stock || !stock.TryGetValue<bool>(out var vanilla) || vanilla) continue;
            var label = ModLabel(row["title"]) ?? ModLabel(row["id"]);
            if (label is not null) mods.Add(label);
        }
        metadata["mods"] = JsonSerializer.SerializeToNode(mods);

        static string? ModLabel(JsonNode? field)
        {
            if (field is not JsonValue value || !value.TryGetValue<string>(out var text)) return null;
            var label = text.Trim();
            return label.Length is > 0 and <= 80 && !label.Any(char.IsControl) ? label : null;
        }
    }

    internal static JsonObject Generated(string json, string world)
    {
        using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) || version != 1 ||
            !root.TryGetProperty("world", out var source) || source.ValueKind != JsonValueKind.String || WorldPath(source.GetString()!) != WorldPath(world)) throw new InvalidDataException("Generated metadata identity/version mismatch");
        var sourceGuid = Regex.Match(source.GetString()!, @"^\{([0-9A-Fa-f]{16})\}");
        var worldGuid = Regex.Match(world, @"^\{([0-9A-Fa-f]{16})\}");
        if (sourceGuid.Success && worldGuid.Success && !sourceGuid.Groups[1].Value.Equals(worldGuid.Groups[1].Value, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Generated metadata world GUID mismatch");
        if (!root.TryGetProperty("scope", out var scope) || scope.ValueKind != JsonValueKind.String || scope.GetString() != "world_subscene") throw new InvalidDataException("Unsupported content capture scope");
        foreach (var key in new[] { "limitations", "factions", "configuredFactions", "referencedAddonIds" })
            if (root.TryGetProperty(key, out var values) && (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > 256 || values.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String || v.GetString()!.Length > 512))) throw new InvalidDataException("Invalid generated " + key);
        if (root.TryGetProperty("assets", out var assets) && (assets.ValueKind != JsonValueKind.Object || assets.EnumerateObject().Count() > 64 || assets.EnumerateObject().Any(a => a.Name.Length > 80 || a.Value.ValueKind != JsonValueKind.Number || !a.Value.TryGetInt32(out var count) || count < 0))) throw new InvalidDataException("Invalid detected assets");
        if (root.TryGetProperty("entityCount", out var entities) && (entities.ValueKind != JsonValueKind.Number || !entities.TryGetInt32(out var count) || count < 0)) throw new InvalidDataException("Invalid entity count");
        if (root.TryGetProperty("loadedAddons", out var addons) && (addons.ValueKind != JsonValueKind.Array || addons.GetArrayLength() > 512)) throw new InvalidDataException("Invalid loaded addons");
        if (root.TryGetProperty("playerSlots", out var slots))
        {
            if (slots.ValueKind != JsonValueKind.Array || slots.GetArrayLength() > 10000) throw new InvalidDataException("Invalid player slots");
            foreach (var slot in slots.EnumerateArray())
            {
                if (slot.ValueKind != JsonValueKind.Object || !slot.TryGetProperty("index", out var index) || index.ValueKind != JsonValueKind.Number || !index.TryGetInt32(out var n) || n < 0 || n >= 10000) throw new InvalidDataException("Invalid player slot index");
                foreach (var key in new[] { "group", "faction", "prefab", "name", "loadoutFaction" })
                    if (!slot.TryGetProperty(key, out var text) || text.ValueKind != JsonValueKind.String || text.GetString()!.Length > 512 || text.GetString()!.Any(char.IsControl)) throw new InvalidDataException("Invalid player slot " + key);
            }
        }
        var result = new JsonObject();
        if (root.TryGetProperty("startHours", out var hours))
        {
            if (hours.ValueKind != JsonValueKind.Array || hours.GetArrayLength() is < 1 or > 24 ||
                hours.EnumerateArray().Any(h => h.ValueKind != JsonValueKind.Number || !h.TryGetInt32(out var n) || n is < 0 or > 23))
                throw new InvalidDataException("Invalid mission start hours");
            result["startHours"] = JsonNode.Parse(hours.GetRawText());
        }
        if (root.TryGetProperty("randomStartTime", out var random))
            result["randomStartTime"] = random.ValueKind == JsonValueKind.True && !result.ContainsKey("startHours") ? true : throw new InvalidDataException("Invalid random start time");
        if (root.TryGetProperty("allowHeaderTime", out var allow))
            result["allowHeaderTime"] = allow.ValueKind is JsonValueKind.True or JsonValueKind.False ? allow.GetBoolean() : throw new InvalidDataException("Invalid scenario time flag");
        foreach (var key in new[] { "schemaVersion", "world", "scope", "limitations", "entityCount", "factions", "configuredFactions", "referencedAddonIds", "assets", "loadedAddons", "playerSlots" })
            if (root.TryGetProperty(key, out var value)) result[key] = JsonNode.Parse(value.GetRawText());
        // Never use these counts as author-confirmed assets, player slots, or complete dependencies.
        return result;
    }
    internal static string WorldPath(string world) => Regex.Replace(world.Replace('\\', '/'), @"^(?:\{[0-9A-Fa-f]{16}\}|\$[^:]+:)", "").ToLowerInvariant();
}
