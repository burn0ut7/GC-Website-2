using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

namespace GC.Infrastructure;

public static class LocalMissionImport
{
    // Explicit local CLI input only; never read file paths supplied by remote catalogue metadata.
    public static string WithHeader(string manifest, string header, string? generated = null)
    {
        var root=System.Text.Json.Nodes.JsonNode.Parse(manifest)?.AsObject() ?? throw new InvalidDataException("Invalid local manifest");
        var parsed=CatalogSync.Parse("local.conf",header,"{0000000000000001}",null,"local",generated);
        if(!string.Equals(root["world"]?.GetValue<string>(),parsed.Metadata.GetProperty("world").GetString(),StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Local manifest and header refer to different worlds");
        foreach(var field in parsed.Metadata.EnumerateObject())root[field.Name]=System.Text.Json.Nodes.JsonNode.Parse(field.Value.GetRawText());
        root["name"]=parsed.Name;root["author"]=parsed.Author;root["description"]=parsed.Description;root["maxPlayers"]=parsed.MaxPlayers;
        return root.ToJsonString();
    }

    // Admin CLI only. World-only definitions are visible but cannot be launched as scenarios.
    public static async Task<Guid> Import(NpgsqlDataSource db, string json, bool replaceList, CancellationToken ct=default)
    {
        using var doc=JsonDocument.Parse(json);var root=doc.RootElement;
        string Text(string key)=>root.TryGetProperty(key,out var value)?value.GetString()??"":"";
        var key=Text("key");var name=Text("name");var world=Text("world");
        var match=Regex.Match(world,@"^\{([A-Fa-f0-9]{16})\}.+\.ent$");
        if(!Regex.IsMatch(key,"^[a-z0-9-]{1,80}$")||name.Length is 0 or >200||!match.Success)throw new ArgumentException("Invalid local mission identity");
        var tags=root.GetProperty("tags").EnumerateArray().Select(x=>x.GetString()??"").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var aliases=root.GetProperty("worldAliases").EnumerateArray().Select(x=>x.GetString()??"").ToArray();
        if(tags.Length>32||tags.Any(x=>string.IsNullOrWhiteSpace(x)||x.Length>80)||aliases.Length>10||aliases.Any(x=>!Regex.IsMatch(x,@"^\$[A-Za-z0-9_]+:.+\.ent$")))throw new ArgumentException("Invalid mission tags or aliases");
        int? max=root.TryGetProperty("maxPlayers",out var count)&&count.ValueKind!=JsonValueKind.Null?count.GetInt32():null;
        if(max is <=0 or >10000)throw new ArgumentException("Invalid player capacity");
        var data=MissionMetadata.Normalize(root,max);
        var source="local/"+key;var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        data["world"]=world;data["worldAliases"]=JsonSerializer.SerializeToNode(aliases);data["kind"]="test";data["sourceKind"]="local";
        if(root.TryGetProperty("generated",out var generated))MissionMetadata.ApplyGenerated(data,generated.GetRawText(),world);
        tags=MissionMetadata.SearchTags(data);
        var metadata=JsonSerializer.Serialize(new{data});
        await using var connection=await db.OpenConnectionAsync(ct);await using var tx=await connection.BeginTransactionAsync(ct);
        await using(var gate=new NpgsqlCommand("SELECT pg_advisory_xact_lock(714824122)",connection,tx))await gate.ExecuteNonQueryAsync(ct);
        if(replaceList)await using(var hide=new NpgsqlCommand("UPDATE gc.catalog_missions SET listed=false",connection,tx))await hide.ExecuteNonQueryAsync(ct);
        await using(var src=new NpgsqlCommand("INSERT INTO gc.catalog_sources(repository,branch,checked_at,succeeded_at) VALUES($1,'local',now(),now()) ON CONFLICT(repository) DO UPDATE SET checked_at=now(),succeeded_at=now(),error=NULL",connection,tx)){src.Parameters.AddWithValue(source);await src.ExecuteNonQueryAsync(ct);}
        await using var q=new NpgsqlCommand("""
            INSERT INTO gc.catalog_missions(id,repository,scenario_guid,world_guid,scenario_path,name,author,description,tags,max_players,metadata,source_hash,available,listed)
            VALUES($1,$2,'',$3,'',$4,$5,$6,$7,$8,$9::jsonb,$10,false,true)
            ON CONFLICT(repository,scenario_guid) DO UPDATE SET world_guid=excluded.world_guid,name=excluded.name,author=excluded.author,
                description=excluded.description,tags=excluded.tags,max_players=excluded.max_players,metadata=excluded.metadata,
                source_hash=excluded.source_hash,listed=true,
                updated_at=CASE WHEN gc.catalog_missions.source_hash<>excluded.source_hash THEN now() ELSE gc.catalog_missions.updated_at END RETURNING id
            """,connection,tx);
        q.Parameters.AddWithValue(Guid.NewGuid());q.Parameters.AddWithValue(source);q.Parameters.AddWithValue(match.Groups[1].Value.ToUpperInvariant());
        q.Parameters.AddWithValue(name);q.Parameters.AddWithValue(Text("author"));q.Parameters.AddWithValue(Text("description"));q.Parameters.AddWithValue(tags);
        q.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Integer,(object?)max??DBNull.Value);q.Parameters.AddWithValue(metadata);q.Parameters.AddWithValue(hash);
        var id=(Guid)(await q.ExecuteScalarAsync(ct))!;await tx.CommitAsync(ct);return id;
    }
}
