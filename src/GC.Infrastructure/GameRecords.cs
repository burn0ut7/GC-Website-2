using System.Text.Json;
using Npgsql;

namespace GC.Infrastructure;

public static class GameRecords
{
    // A projected player row is already the latest revision for one server/session/identity.
    // Sum recorded keys only; absent measurements must stay absent.
    private const string TotalCounters = """
        (SELECT coalesce(jsonb_object_agg(key,total),'{}'::jsonb)
         FROM (SELECT key,sum(value::numeric) AS total
               FROM participants CROSS JOIN LATERAL jsonb_each_text(counters::jsonb)
               WHERE value IS NOT NULL GROUP BY key) sums)
        """;

    private static JsonElement Json(NpgsqlDataReader reader,int column) =>
        JsonSerializer.Deserialize<JsonElement>(reader.GetString(column));

    private const string SessionDates = """
        LEFT JOIN gc.catalog_sessions s USING(server_id,mission_id)
        LEFT JOIN gc.mission_batches b ON b.server_id=p.server_id AND b.mission_id=p.mission_id AND b.stream='stats' AND b.revision=p.revision
        """;

    public static async Task<object?> Player(NpgsqlDataSource db,string identity,CancellationToken ct)
    {
        await using var q=db.CreateCommand($"""
            WITH participants AS (SELECT * FROM gc.mission_players WHERE identity=$1)
            SELECT p.name,(SELECT count(*) FROM participants),{TotalCounters}
            FROM participants p {SessionDates}
            ORDER BY coalesce(s.started_at,b.started_at) DESC NULLS LAST,p.server_id,p.mission_id LIMIT 1
            """);
        q.Parameters.AddWithValue(identity);
        await using var r=await q.ExecuteReaderAsync(ct);
        if(!await r.ReadAsync(ct))return null;
        return new { identity,name=r.GetString(0),sessions=r.GetInt64(1),totals=Json(r,2) };
    }

    public static async Task<object> PlayerSessions(NpgsqlDataSource db,string identity,int offset,CancellationToken ct)
    {
        await using var q=db.CreateCommand($"""
            SELECT p.server_id,p.mission_id,coalesce(s.started_at,b.started_at) AS started,m.id,m.name,s.metadata->>'missionName',
                   s.metadata->>'outcome',p.counters,p.details,s.metadata->>'serverName'
            FROM gc.mission_players p {SessionDates}
            LEFT JOIN gc.catalog_missions m ON m.id=s.catalog_id AND m.listed
            WHERE p.identity=$1 ORDER BY started DESC NULLS LAST,p.server_id,p.mission_id LIMIT 51 OFFSET $2
            """);
        q.Parameters.AddWithValue(identity);q.Parameters.AddWithValue(offset);
        var sessions=new List<object>();
        await using var r=await q.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct))
        {
            if(sessions.Count==50)return new { sessions,nextOffset=(int?)(offset+50) };
            sessions.Add(new {serverId=r.GetGuid(0),sessionId=r.GetGuid(1),startedAt=r.IsDBNull(2)?(long?)null:r.GetInt64(2),
                catalogId=r.IsDBNull(3)?(Guid?)null:r.GetGuid(3),missionName=r.IsDBNull(4)?r.IsDBNull(5)?null:r.GetString(5):r.GetString(4),
                outcome=r.IsDBNull(6)?null:r.GetString(6),counters=Json(r,7),details=r.IsDBNull(8)?(JsonElement?)null:Json(r,8),
                serverName=r.IsDBNull(9)?null:r.GetString(9)});
        }
        return new {sessions,nextOffset=(int?)null};
    }

    public static async Task<object> Players(NpgsqlDataSource db,string search,int offset,CancellationToken ct)
    {
        await using var q=db.CreateCommand($"""
            WITH people AS (
                SELECT DISTINCT ON (p.identity) p.identity,p.name,count(*) OVER (PARTITION BY p.identity) AS sessions
                FROM gc.mission_players p {SessionDates}
                ORDER BY p.identity,coalesce(s.started_at,b.started_at) DESC NULLS LAST,p.server_id,p.mission_id)
            SELECT identity,name,sessions FROM people WHERE strpos(lower(name),lower($1))>0 OR identity=$1
            ORDER BY lower(name),identity LIMIT 51 OFFSET $2
            """);
        q.Parameters.AddWithValue(search);q.Parameters.AddWithValue(offset);
        var players=new List<object>();
        await using var r=await q.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct))
        {
            if(players.Count==50)return new {players,nextOffset=(int?)(offset+50)};
            players.Add(new {identity=r.GetString(0),name=r.GetString(1),sessions=r.GetInt64(2)});
        }
        return new {players,nextOffset=(int?)null};
    }

    public static async Task<object?> Session(NpgsqlDataSource db,Guid server,Guid session,CancellationToken ct)
    {
        var summary=await Missions.Summary(db,server,session,ct);
        await using var q=db.CreateCommand($"""
            WITH participants AS (SELECT * FROM gc.mission_players WHERE server_id=$1 AND mission_id=$2)
            SELECT s.mission_id,m.id,m.name,s.started_at,s.metadata,(SELECT count(*) FROM participants),{TotalCounters},
                ARRAY(SELECT ref FROM (
                    SELECT actor AS ref FROM gc.session_events WHERE server_id=$1 AND mission_id=$2
                    UNION SELECT target FROM gc.session_events WHERE server_id=$1 AND mission_id=$2
                    UNION SELECT c->>'identity' FROM jsonb_array_elements(s.metadata->'commanders') c
                ) refs WHERE ref<>'' AND EXISTS(SELECT 1 FROM gc.mission_players known WHERE known.identity=ref))
            FROM (SELECT 1) anchor LEFT JOIN gc.catalog_sessions s ON s.server_id=$1 AND s.mission_id=$2
            LEFT JOIN gc.catalog_missions m ON m.id=s.catalog_id AND m.listed
            """);
        q.Parameters.AddWithValue(server);q.Parameters.AddWithValue(session);
        await using var r=await q.ExecuteReaderAsync(ct);await r.ReadAsync(ct);
        if(summary is null && r.IsDBNull(0) && r.GetInt64(5)==0)return null;
        return new {serverId=server,sessionId=session,catalogId=r.IsDBNull(1)?(Guid?)null:r.GetGuid(1),
            missionName=r.IsDBNull(2)?null:r.GetString(2),startedAt=r.IsDBNull(3)?(long?)null:r.GetInt64(3),
            metadata=r.IsDBNull(4)?(JsonElement?)null:Json(r,4),recordedPlayers=r.GetInt64(5),totals=Json(r,6),profileIdentities=r.GetFieldValue<string[]>(7),summary};
    }
}
