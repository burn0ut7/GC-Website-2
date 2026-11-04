using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;

namespace GC.Infrastructure;

public sealed record CatalogSource(string Repository, string Branch);

/// <summary>Commit-pinned snapshots. A failed/incomplete fetch never removes catalogue entries.</summary>
public sealed class CatalogSync(HttpClient http)
{
    private const int ParserVersion=11;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task Sync(NpgsqlDataSource db,CatalogSource source,CancellationToken ct)
    {
        if(!Regex.IsMatch(source.Repository,@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$") ||
            !Regex.IsMatch(source.Branch,@"^[A-Za-z0-9_./-]+$") || source.Branch.Contains("..")) throw new ArgumentException("Invalid catalogue source");
        await using var connection=await db.OpenConnectionAsync(ct);
        // Dedicated connection owns a per-source session lock; HTTP does not hold a database transaction.
        await using var gate=new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtextextended($1,0))",connection);
        gate.Parameters.AddWithValue("catalog:"+source.Repository);
        if(await gate.ExecuteScalarAsync(ct) is not true)return;
        try {
            await using(var setup=new NpgsqlCommand("INSERT INTO gc.catalog_sources(repository,branch) VALUES($1,$2) ON CONFLICT(repository) DO NOTHING",connection)) {
                setup.Parameters.AddWithValue(source.Repository);setup.Parameters.AddWithValue(source.Branch);await setup.ExecuteNonQueryAsync(ct);
            }
            try { await Refresh(db,connection,source,ct); }
            catch(Exception e) when(e is HttpRequestException or IOException or JsonException or InvalidDataException or TaskCanceledException) {
                if(ct.IsCancellationRequested)throw;
                // No response bodies, credentials or remote exception strings in the status endpoint.
                var code=e is HttpRequestException h ? "github_"+(h.StatusCode?.ToString()??"unavailable") : e is InvalidDataException ? "invalid_source_snapshot" : "source_unavailable";
                await using var fail=new NpgsqlCommand("UPDATE gc.catalog_sources SET checked_at=now(),error=$2 WHERE repository=$1",connection);
                fail.Parameters.AddWithValue(source.Repository);fail.Parameters.AddWithValue(code);await fail.ExecuteNonQueryAsync(ct);
                throw new InvalidOperationException($"Catalogue {source.Repository}: {code}",e);
            }
        } finally {
            await using var unlock=new NpgsqlCommand("SELECT pg_advisory_unlock(hashtextextended($1,0))",connection);
            unlock.Parameters.AddWithValue("catalog:"+source.Repository);await unlock.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private async Task Refresh(NpgsqlDataSource db,NpgsqlConnection connection,CatalogSource source,CancellationToken ct)
    {
        var api="https://api.github.com/repos/"+source.Repository;
        using var commit=JsonDocument.Parse(await Read(api+"/git/ref/heads/"+EscapePath(source.Branch),2*1024*1024,ct));
        var sha=commit.RootElement.GetProperty("object").GetProperty("sha").GetString()!;
        if(!Regex.IsMatch(sha,"^[0-9a-f]{40}$"))throw new InvalidDataException("Invalid commit");
        await using(var prior=new NpgsqlCommand("SELECT commit_sha FROM gc.catalog_sources s WHERE repository=$1 AND branch=$2 AND NOT EXISTS(SELECT 1 FROM gc.catalog_missions m WHERE m.repository=s.repository AND m.available AND (m.metadata->'data'->>'parserVersion') IS DISTINCT FROM $3)",connection)) {
            prior.Parameters.AddWithValue(source.Repository);prior.Parameters.AddWithValue(source.Branch);
            prior.Parameters.AddWithValue(ParserVersion.ToString());
            if(await prior.ExecuteScalarAsync(ct) is string previous && previous==sha) {
                await using var checkedAt=new NpgsqlCommand("UPDATE gc.catalog_sources SET checked_at=now(),succeeded_at=now(),error=NULL WHERE repository=$1",connection);
                checkedAt.Parameters.AddWithValue(source.Repository);await checkedAt.ExecuteNonQueryAsync(ct);return;
            }
        }
        using var tree=JsonDocument.Parse(await Read(api+"/git/trees/"+sha+"?recursive=1",32*1024*1024,ct));
        if(tree.RootElement.GetProperty("truncated").GetBoolean())throw new InvalidDataException("Incomplete tree");
        var files=tree.RootElement.GetProperty("tree").EnumerateArray().Where(x=>x.GetProperty("type").GetString()=="blob")
            .ToDictionary(x=>x.GetProperty("path").GetString()!,x=>x.GetProperty("sha").GetString()!,StringComparer.Ordinal);
        var paths=files.Keys.Where(p=>(p.StartsWith("Missions/",StringComparison.OrdinalIgnoreCase)||p.StartsWith("Configs/Missions/",StringComparison.OrdinalIgnoreCase))&&p.EndsWith(".conf",StringComparison.OrdinalIgnoreCase)).Order().ToArray();
        if(paths.Length==0||paths.Length>5000)throw new InvalidDataException("Unexpected mission count");
        var old=new Dictionary<string,CatalogMission>();
        await using(var cached=new NpgsqlCommand("SELECT metadata->>'snapshot' FROM gc.catalog_missions WHERE repository=$1",connection)) {
            cached.Parameters.AddWithValue(source.Repository);await using var r=await cached.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))if(!r.IsDBNull(0)) {var m=JsonSerializer.Deserialize<CatalogMission>(r.GetString(0),Json)!;old[m.ScenarioPath]=m;}
        }
        using var throttle=new SemaphoreSlim(4);
        var issues=new System.Collections.Concurrent.ConcurrentBag<object>();
        var tasks=paths.Select(async path=>{
            await throttle.WaitAsync(ct);
            try {
                if(!files.TryGetValue(path+".meta",out var metaHash))throw new InvalidDataException("Missing scenario identity");
                var sidecar=path+".metadata.json";
                var raw="https://raw.githubusercontent.com/"+source.Repository+"/"+sha+"/";
                var content=await Read(raw+EscapePath(path),1024*1024,ct);
                var worldPath=MissionMetadata.WorldPath(MissionConfig.Read(content).Text("World"));
                var generatedPath=files.Keys.SingleOrDefault(p=>p.Equals(worldPath+".metadata.json",StringComparison.OrdinalIgnoreCase));
                var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ParserVersion+files[path]+metaHash+files.GetValueOrDefault(sidecar)+(generatedPath is null?"":files[generatedPath]))));
                if(old.TryGetValue(path,out var known)&&known.SourceHash==hash)return known;
                var meta=await Read(raw+EscapePath(path+".meta"),1024*1024,ct);
                var extra=files.ContainsKey(sidecar)?await Read(raw+EscapePath(sidecar),1024*1024,ct):null;
                var generated=generatedPath is null?null:await Read(raw+EscapePath(generatedPath),8*1024*1024,ct);
                return Parse(path,content,meta,extra,hash,generated);
            } catch(Exception error) when(error is InvalidDataException or JsonException) {
                issues.Add(new{path,reason="invalid_mission_header",retained=old.ContainsKey(path)});
                return old.GetValueOrDefault(path);
            } finally {throttle.Release();}
        }).ToArray();
        var missions=(await Task.WhenAll(tasks)).Where(m=>m is not null).Select(m=>m!).ToArray();
        if(missions.Length==0)throw new InvalidDataException("No valid mission headers");
        if(missions.Select(m=>m.ScenarioGuid).Distinct().Count()!=missions.Length)throw new InvalidDataException("Duplicate scenario identity");
        await using var transaction=await connection.BeginTransactionAsync(ct);
        await using(var retire=new NpgsqlCommand("UPDATE gc.catalog_missions SET available=false WHERE repository=$1",connection,transaction)) {
            retire.Parameters.AddWithValue(source.Repository);await retire.ExecuteNonQueryAsync(ct);
        }
        foreach(var m in missions) {
            var metadata=JsonSerializer.Serialize(new { data=m.Metadata,snapshot=m },Json);
            await using var save=new NpgsqlCommand("""
                INSERT INTO gc.catalog_missions(id,repository,scenario_guid,world_guid,scenario_path,name,author,description,tags,max_players,metadata,source_hash)
                VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12)
                ON CONFLICT(repository,scenario_guid) DO UPDATE SET world_guid=excluded.world_guid,scenario_path=excluded.scenario_path,
                    name=excluded.name,author=excluded.author,description=excluded.description,tags=excluded.tags,max_players=excluded.max_players,
                    metadata=excluded.metadata,source_hash=excluded.source_hash,available=true,
                    updated_at=CASE WHEN gc.catalog_missions.source_hash<>excluded.source_hash THEN now() ELSE gc.catalog_missions.updated_at END
                """,connection,transaction);
            save.Parameters.AddWithValue(Guid.NewGuid());save.Parameters.AddWithValue(source.Repository);save.Parameters.AddWithValue(m.ScenarioGuid);
            save.Parameters.AddWithValue(m.WorldGuid);save.Parameters.AddWithValue(m.ScenarioPath);save.Parameters.AddWithValue(m.Name);save.Parameters.AddWithValue(m.Author);
            save.Parameters.AddWithValue(m.Description);save.Parameters.AddWithValue(m.Tags);save.Parameters.AddWithValue(NpgsqlDbType.Integer,(object?)m.MaxPlayers??DBNull.Value);
            save.Parameters.AddWithValue(NpgsqlDbType.Jsonb,metadata);save.Parameters.AddWithValue(m.SourceHash);await save.ExecuteNonQueryAsync(ct);
        }
        await using(var status=new NpgsqlCommand("UPDATE gc.catalog_sources SET branch=$2,commit_sha=$3,checked_at=now(),succeeded_at=now(),error=NULL,issues=$4::jsonb WHERE repository=$1",connection,transaction)) {
            status.Parameters.AddWithValue(source.Repository);status.Parameters.AddWithValue(source.Branch);status.Parameters.AddWithValue(sha);status.Parameters.AddWithValue(JsonSerializer.Serialize(issues));await status.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
    }

    public static CatalogMission Parse(string path,string conf,string meta,string? extra,string hash,string? generated=null)
    {
        var header=MissionConfig.Read(conf);
        var scenario=Regex.Match(meta,@"\{([0-9A-Fa-f]{16})\}");
        var world=header.Text("World");var worldId=Regex.Match(world,@"^\{([0-9A-Fa-f]{16})\}");
        var name=header.Text("m_sName");
        if(!scenario.Success||!worldId.Success||string.IsNullOrWhiteSpace(name))throw new InvalidDataException("Incomplete mission header");
        var prefix=Regex.Match(name,@"^(COOP|COTVT|TVT|LOL|TRNG|TEST|BROKEN)(?=\b|[0-9])",RegexOptions.IgnoreCase);
        var authored=MissionMetadata.FromHeader(header);
        if(extra is not null)
        {
            using var sidecar=JsonDocument.Parse(extra);
            if(sidecar.RootElement.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("Sidecar must be an object");
            foreach(var field in sidecar.RootElement.EnumerateObject())authored[field.Name]=System.Text.Json.Nodes.JsonNode.Parse(field.Value.GetRawText());
        }
        int? max=null;
        if(header.Text("m_iPlayerCount") is {Length:>0} count)
        {
            if(!int.TryParse(count,out var n)||n is <=0 or >10000)throw new InvalidDataException("Invalid player capacity");
            max=n;
        }
        using var input=JsonDocument.Parse(authored.ToJsonString());
        var metadata=MissionMetadata.Normalize(input.RootElement,max,prefix.Success?prefix.Value.ToUpperInvariant():null);
        metadata["parserVersion"]=ParserVersion;metadata["world"]=world;
        metadata["sourceKind"]="repository";
        metadata["authoredSource"]=extra is not null?"header-and-sidecar":header.Object("m_GC_Catalog") is not null?"lobby2-header":header.Object("m_GC_Metadata") is not null?"gc-header":"standard-header";
        if(generated is not null)MissionMetadata.ApplyGenerated(metadata,generated,world,header);
        var tags=MissionMetadata.SearchTags(metadata);
        return new(scenario.Groups[1].Value.ToUpperInvariant(),worldId.Groups[1].Value.ToUpperInvariant(),path,name,header.Text("m_sAuthor"),header.Text("m_sDescription"),tags,max,JsonSerializer.SerializeToElement(metadata),hash);
    }

    private static string EscapePath(string path)=>string.Join('/',path.Split('/').Select(Uri.EscapeDataString));
    private async Task<string> Read(string url,int maximum,CancellationToken ct)
    {
        using var request=new HttpRequestMessage(HttpMethod.Get,url);
        request.Headers.UserAgent.ParseAdd("GlobalConflicts-Catalog/1.0");
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);response.EnsureSuccessStatusCode();
        if(response.Content.Headers.ContentLength>maximum)throw new InvalidDataException("Source too large");
        using var memory=new MemoryStream();await using var stream=await response.Content.ReadAsStreamAsync(ct);var buffer=new byte[16384];int count;
        while((count=await stream.ReadAsync(buffer,ct))>0){if(memory.Length+count>maximum)throw new InvalidDataException("Source too large");await memory.WriteAsync(buffer.AsMemory(0,count),ct);}
        return Encoding.UTF8.GetString(memory.ToArray());
    }
}
