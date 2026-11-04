using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace GC.Infrastructure;

public sealed record CatalogMission(string ScenarioGuid, string WorldGuid, string ScenarioPath,
    string Name, string Author, string Description, string[] Tags, int? MaxPlayers, JsonElement Metadata, string SourceHash);

public static class MissionCatalog
{
    public static async Task<object> List(NpgsqlDataSource db, string? search, string? tag, int offset, CancellationToken ct)
    {
        if (offset < 0 || offset > 100000 || search?.Length > 200 || tag?.Length > 80) throw new ArgumentException("Invalid filter");
        await using var q = db.CreateCommand("""
            SELECT m.id,m.name,m.author,m.tags,m.max_players,m.available,m.metadata->'data',m.updated_at,
                (SELECT count(*) FROM gc.catalog_sessions s WHERE s.catalog_id=m.id AND s.metadata->>'status' IN ('running','completed')),
                (SELECT max(started_at) FROM gc.catalog_sessions s WHERE s.catalog_id=m.id AND s.metadata->>'status' IN ('running','completed')),
                m.scenario_guid,m.scenario_path,m.world_guid
            FROM gc.catalog_missions m
            WHERE m.listed AND ($1='' OR strpos(lower(m.name||' '||m.author),lower($1))>0)
                AND ($2='' OR $2=ANY(m.tags))
            ORDER BY m.available DESC,m.name,m.id LIMIT 51 OFFSET $3
            """);
        q.Parameters.AddWithValue(search ?? ""); q.Parameters.AddWithValue(tag ?? ""); q.Parameters.AddWithValue(offset);
        var rows = new List<object>();
        await using var r = await q.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) {
            if (rows.Count == 50) return new { missions=rows, nextOffset=(int?)(offset+50) };
            rows.Add(new { id=r.GetGuid(0),name=r.GetString(1),author=r.GetString(2),tags=r.GetFieldValue<string[]>(3),
                maxPlayers=r.IsDBNull(4)?(int?)null:r.GetInt32(4),available=r.GetBoolean(5),metadata=JsonSerializer.Deserialize<JsonElement>(r.GetString(6)),
                updatedAt=r.GetDateTime(7),playCount=r.GetInt64(8),lastPlayedAt=r.IsDBNull(9)?(long?)null:r.GetInt64(9),
                scenarioId=r.GetString(10)==""?null:"{"+r.GetString(10)+"}"+r.GetString(11),worldGuid=r.GetString(12) });
        }
        return new { missions=rows,nextOffset=(int?)null };
    }

    public static async Task<object?> Detail(NpgsqlDataSource db, Guid id, CancellationToken ct)
    {
        await using var q=db.CreateCommand("SELECT repository,scenario_guid,world_guid,scenario_path,name,author,description,tags,max_players,metadata->'data',available,updated_at FROM gc.catalog_missions WHERE id=$1");
        q.Parameters.AddWithValue(id); await using var r=await q.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new { id,repository=r.GetString(0),scenarioGuid=r.GetString(1),worldGuid=r.GetString(2),scenarioId=r.GetString(1)==""?null:"{"+r.GetString(1)+"}"+r.GetString(3),
            scenarioPath=r.GetString(3),name=r.GetString(4),author=r.GetString(5),description=r.GetString(6),tags=r.GetFieldValue<string[]>(7),
            maxPlayers=r.IsDBNull(8)?(int?)null:r.GetInt32(8),metadata=JsonSerializer.Deserialize<JsonElement>(r.GetString(9)),available=r.GetBoolean(10),updatedAt=r.GetDateTime(11) };
    }

    public static async Task<object> Status(NpgsqlDataSource db,CancellationToken ct)
    {
        await using var q=db.CreateCommand("SELECT repository,branch,commit_sha,checked_at,succeeded_at,error,issues FROM gc.catalog_sources s WHERE EXISTS(SELECT 1 FROM gc.catalog_missions m WHERE m.repository=s.repository AND m.listed) ORDER BY repository");
        var sources=new List<object>(); await using(var r=await q.ExecuteReaderAsync(ct))
            while(await r.ReadAsync(ct)) sources.Add(new { repository=r.GetString(0),branch=r.GetString(1),commit=r.IsDBNull(2)?null:r.GetString(2),
                checkedAt=r.IsDBNull(3)?(DateTime?)null:r.GetDateTime(3),succeededAt=r.IsDBNull(4)?(DateTime?)null:r.GetDateTime(4),error=r.IsDBNull(5)?null:r.GetString(5),issues=JsonSerializer.Deserialize<JsonElement>(r.GetString(6)) });
        await using var tags=db.CreateCommand("SELECT DISTINCT unnest(tags) tag FROM gc.catalog_missions WHERE listed ORDER BY tag");
        var values=new List<string>();await using(var r=await tags.ExecuteReaderAsync(ct)) while(await r.ReadAsync(ct))values.Add(r.GetString(0));
        return new { sources,tags=values };
    }

    public static async Task<object> Statistics(NpgsqlDataSource db,Guid catalog,Guid? server,CancellationToken ct)
    {
        await using var q=db.CreateCommand("""
            WITH sessions AS (SELECT * FROM gc.catalog_sessions WHERE catalog_id=$1 AND ($2::uuid IS NULL OR server_id=$2)),
            players AS (SELECT p.* FROM gc.mission_players p JOIN sessions s USING(server_id,mission_id))
            SELECT (SELECT count(*) FROM sessions),(SELECT count(DISTINCT identity) FROM players),
                (SELECT count(DISTINCT (t.server_id,t.recording_id)) FROM gc.telemetry_batches t JOIN sessions s USING(server_id,mission_id)),
                (SELECT sum((counters::jsonb->>'playSeconds')::double precision) FROM players),
                (SELECT sum((counters::jsonb->>'kills')::bigint) FROM players),
                (SELECT sum((counters::jsonb->>'deaths')::bigint) FROM players)
            """);
        q.Parameters.AddWithValue(catalog);q.Parameters.AddWithValue(NpgsqlDbType.Uuid,(object?)server??DBNull.Value);
        await using var r=await q.ExecuteReaderAsync(ct);await r.ReadAsync(ct);
        return new{sessions=r.GetInt64(0),players=r.GetInt64(1),recordings=r.GetInt64(2),
            playerSeconds=r.IsDBNull(3)?(double?)null:r.GetDouble(3),kills=r.IsDBNull(4)?(decimal?)null:r.GetFieldValue<decimal>(4),deaths=r.IsDBNull(5)?(decimal?)null:r.GetFieldValue<decimal>(5)};
    }

    public static async Task<object> Sessions(NpgsqlDataSource db,Guid? catalog,Guid? server,int offset,CancellationToken ct)
    {
        if(offset<0||offset>100000)throw new ArgumentException("Invalid page");
        await using var q=db.CreateCommand("""
            WITH ids AS (
                SELECT server_id,mission_id FROM gc.catalog_sessions WHERE ($1::uuid IS NULL OR catalog_id=$1) AND ($2::uuid IS NULL OR server_id=$2)
                UNION SELECT server_id,mission_id FROM gc.mission_batches WHERE $1 IS NULL AND ($2 IS NULL OR server_id=$2)
                UNION SELECT server_id,mission_id FROM gc.telemetry_batches WHERE $1 IS NULL AND mission_id IS NOT NULL AND ($2 IS NULL OR server_id=$2)
            )
            SELECT i.server_id,i.mission_id,coalesce(s.started_at,b.started_at) AS started,s.metadata,coalesce(s.link_status,'unlinked'),
                ARRAY(SELECT DISTINCT recording_id FROM gc.telemetry_batches t WHERE t.server_id=i.server_id AND t.mission_id=i.mission_id ORDER BY recording_id LIMIT 50),
                m.id,m.name
            FROM ids i LEFT JOIN gc.catalog_sessions s USING(server_id,mission_id)
            LEFT JOIN LATERAL (SELECT min(started_at) AS started_at FROM gc.mission_batches b WHERE s.started_at IS NULL AND b.server_id=i.server_id AND b.mission_id=i.mission_id) b ON true
            LEFT JOIN gc.catalog_missions m ON m.id=s.catalog_id AND m.listed
            ORDER BY started DESC NULLS LAST,i.server_id,i.mission_id LIMIT 51 OFFSET $3
            """);
        q.Parameters.AddWithValue(NpgsqlDbType.Uuid,(object?)catalog??DBNull.Value);q.Parameters.AddWithValue(NpgsqlDbType.Uuid,(object?)server??DBNull.Value);q.Parameters.AddWithValue(offset);
        var rows=new List<object>();await using var r=await q.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct)) {
            if(rows.Count==50)return new {sessions=rows,nextOffset=(int?)(offset+50)};
            rows.Add(new{serverId=r.GetGuid(0),missionId=r.GetGuid(1),startedAt=r.IsDBNull(2)?(long?)null:r.GetInt64(2),metadata=r.IsDBNull(3)?(JsonElement?)null:JsonSerializer.Deserialize<JsonElement>(r.GetString(3)),linkStatus=r.GetString(4),recordingIds=r.GetFieldValue<Guid[]>(5),
                catalogId=r.IsDBNull(6)?(Guid?)null:r.GetGuid(6),missionName=r.IsDBNull(7)?null:r.GetString(7)});
        }
        return new{sessions=rows,nextOffset=(int?)null};
    }
}
