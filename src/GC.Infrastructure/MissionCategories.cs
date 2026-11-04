using System.Text.Json;
namespace GC.Infrastructure;

// Shared authored metadata for local manifests and repository sidecars.
public sealed record MissionCategories(string? MissionType, string[] Objectives, string[] Mods, string[] AssetTypes)
{
    public static readonly string[] Types=["COOP","TVT","COTVT","TRNG","LOL","TEST","BROKEN"];
    public static readonly string[] ObjectiveSuggestions=["Defense","Attack & Defend","Assault","Convoy","HVT","Sweep","Hostage","Delay","Search & Rescue"];
    public static MissionCategories Parse(JsonElement root,string? fallbackType=null)
    {
        string[] Values(string key)
        {
            if(!root.TryGetProperty(key,out var value))return [];
            if(value.ValueKind!=JsonValueKind.Array||value.GetArrayLength()>(key=="mods"?512:64))throw new InvalidDataException("Invalid "+key);
            var result=new List<string>();
            foreach(var item in value.EnumerateArray())
            {
                if(item.ValueKind!=JsonValueKind.String)throw new InvalidDataException("Invalid "+key);
                var text=item.GetString()!.Trim();
                if(text.Length is 0 or >80||text.Any(char.IsControl))throw new InvalidDataException("Invalid "+key);
                result.Add(text);
            }
            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        var type=fallbackType;
        if(root.TryGetProperty("missionType",out var field)&&field.ValueKind!=JsonValueKind.Null)
        {
            if(field.ValueKind!=JsonValueKind.String)throw new InvalidDataException("Invalid mission type");
            type=field.GetString()?.Trim().ToUpperInvariant();
        }
        if(type is not null&&!Types.Contains(type))throw new InvalidDataException("Unknown mission type");
        var composition = Values("assetTypes").Select(value => value switch {
            "APCs" or "IFVs" => "Mechanized",
            "Cars" or "Cars / light vehicles" or "Trucks" => "Motorized",
            "Tanks" => "Armored",
            "Mortars" => "Artillery",
            "Airmobile" or "Air support" or "Aircraft" or "Transport helicopters" or "Attack helicopters" => "Airborne",
            "Boats" => "Naval",
            _ => value
        }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new(type,Values("objectives"),Values("mods"),composition);
    }
    public string[] SearchTags(IEnumerable<string> tags)=>tags.Concat(MissionType is null?[]:new[]{MissionType})
        .Concat(Objectives).Concat(Mods).Concat(AssetTypes).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}
