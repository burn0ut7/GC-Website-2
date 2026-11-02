using System.Text;
using System.Text.Json;
using Npgsql;

namespace GC.Infrastructure;

public static class Missions
{
    public static async Task<object> List(NpgsqlDataSource database,Guid server,Guid? after,CancellationToken token)
    {
        await using var query=database.CreateCommand("""
            SELECT mission_id FROM (
                SELECT mission_id FROM gc.mission_batches WHERE server_id=$1
                UNION SELECT mission_id FROM gc.telemetry_batches WHERE server_id=$1
            ) m WHERE ($2::uuid IS NULL OR mission_id>$2) ORDER BY mission_id LIMIT 51
            """);
        query.Parameters.AddWithValue(server);query.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Uuid,(object?)after??DBNull.Value);
        var ids=new List<Guid>();await using var reader=await query.ExecuteReaderAsync(token);
        while(await reader.ReadAsync(token))ids.Add(reader.GetGuid(0));
        return new { serverId=server,missionIds=ids.Take(50),nextAfter=ids.Count>50?(Guid?)ids[49]:null };
    }

    public static async Task<object?> Summary(NpgsqlDataSource database,Guid server,Guid mission,CancellationToken token)
    {
        // One repeatable snapshot so latest headers, counts and completeness describe the same revision set.
        await using var connection=await database.OpenConnectionAsync(token);
        await using var transaction=await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead,token);
        var streams=new Dictionary<string,object>();
        foreach(var stream in new[]{"stats","session","history"})
        {
            await using var query=new NpgsqlCommand("""
                SELECT revision,header,finished,source_complete,dropped_records,
                    (SELECT count(*) FROM gc.mission_batches WHERE server_id=$1 AND mission_id=$2 AND stream=$3)
                FROM gc.mission_batches WHERE server_id=$1 AND mission_id=$2 AND stream=$3 ORDER BY revision DESC LIMIT 1
                """,connection,transaction);
            query.Parameters.AddWithValue(server);query.Parameters.AddWithValue(mission);query.Parameters.AddWithValue(stream);
            await using var reader=await query.ExecuteReaderAsync(token);
            if(!await reader.ReadAsync(token)){streams[stream]=new{available=false};continue;}
            using var doc=JsonDocument.Parse(reader.GetString(1));
            var header=doc.RootElement;
            var allowed=stream switch
            {
                "history"=>new[]{"world","startedAt","endedAt","serverName","missionName","author","hostKind","scenarioPath","status","outcome","resultFactions","winningFactions","sessionPerformance","gamePerformance","phases","factions"},
                "stats"=>new[]{"schemaVersion","world","startedAt","durationSeconds","playtimeBasis","minimumPlayersForPlaytime"},
                _=>new[]{"world","startedAt"}
            };
            var metadata=header.EnumerateObject().Where(p=>allowed.Contains(p.Name,StringComparer.Ordinal)).ToDictionary(p=>p.Name,p=>p.Value.Clone());
            var contiguous=reader.GetInt64(5)==(long)reader.GetInt32(0)+1;
            streams[stream]=new{available=true,latestRevision=reader.GetInt32(0),batches=reader.GetInt64(5),closed=reader.GetBoolean(2),
                sourceComplete=stream=="session"?(bool?)null:reader.GetBoolean(3),allBatchesPresent=contiguous,droppedRecords=reader.GetInt32(4),metadata};
        }
        var recordings=new List<Guid>();
        await using(var query=new NpgsqlCommand("SELECT DISTINCT recording_id FROM gc.telemetry_batches WHERE server_id=$1 AND mission_id=$2 ORDER BY recording_id LIMIT 51",connection,transaction))
        {
            query.Parameters.AddWithValue(server);query.Parameters.AddWithValue(mission);
            await using var reader=await query.ExecuteReaderAsync(token);while(await reader.ReadAsync(token))recordings.Add(reader.GetGuid(0));
        }
        var statuses=new List<object>();
        await using(var query=new NpgsqlCommand("""
            SELECT stream,status,count(*) FROM gc.ingest_receipts r WHERE server_id=$1 AND
                ((stream<>'telemetry' AND recording_id=$2) OR (stream='telemetry' AND recording_id IN
                    (SELECT recording_id FROM gc.telemetry_batches WHERE server_id=$1 AND mission_id=$2))) GROUP BY stream,status
            """,connection,transaction))
        {
            query.Parameters.AddWithValue(server);query.Parameters.AddWithValue(mission);
            await using var reader=await query.ExecuteReaderAsync(token);
            while(await reader.ReadAsync(token))statuses.Add(new{stream=reader.GetString(0),status=reader.GetString(1),batches=reader.GetInt64(2)});
        }
        if(statuses.Count==0)return null;
        var types=new Dictionary<string,long>();
        await using(var query=new NpgsqlCommand("SELECT event_type,count(*) FROM gc.session_events WHERE server_id=$1 AND mission_id=$2 GROUP BY event_type",connection,transaction))
        {
            query.Parameters.AddWithValue(server);query.Parameters.AddWithValue(mission);
            await using var reader=await query.ExecuteReaderAsync(token);while(await reader.ReadAsync(token))types.Add(reader.GetString(0),reader.GetInt64(1));
        }
        await using var coverage=new NpgsqlCommand("""
            SELECT count(*),min(sequence),max(sequence),count(DISTINCT actor) FILTER(WHERE event_type='joined' AND actor<>'')
            FROM gc.session_events WHERE server_id=$1 AND mission_id=$2
            """,connection,transaction);
        coverage.Parameters.AddWithValue(server);coverage.Parameters.AddWithValue(mission);
        await using var counts=await coverage.ExecuteReaderAsync(token);await counts.ReadAsync(token);
        var events=counts.GetInt64(0);
        return new {serverId=server,missionId=mission,streams,receipts=statuses,eventTypes=types,eventCount=events,
            eventSequenceContiguous=events==0?(bool?)null:counts.GetInt32(1)==1&&events==counts.GetInt32(2),
            recordedJoiners=counts.GetInt64(3),recordingIds=recordings.Take(50),moreRecordings=recordings.Count>50,
            completeness="Missing streams are unknown, not zero. Check closure, sourceComplete, revision/sequence coverage, drops and rejected/pending receipts together. Telemetry has its own recording completeness. Statistics and session clocks have different origins."};
    }

    public static async Task<object> Players(NpgsqlDataSource database,Guid server,Guid mission,string? after,CancellationToken token)
    {
        if(after?.Length>256)throw new ArgumentException("Invalid player cursor.");
        await using var query=database.CreateCommand("""
            SELECT identity,name,revision,counters FROM gc.mission_players WHERE server_id=$1 AND mission_id=$2
            AND ($3::text IS NULL OR identity COLLATE "C">$3 COLLATE "C") ORDER BY identity COLLATE "C" LIMIT 51
            """);
        query.Parameters.AddWithValue(server);query.Parameters.AddWithValue(mission);
        query.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Text,(object?)after??DBNull.Value);
        var players=new List<object>();string? cursor=null;bool more=false;
        await using var reader=await query.ExecuteReaderAsync(token);
        while(await reader.ReadAsync(token))
        {
            if(players.Count==50){more=true;break;}
            using var doc=JsonDocument.Parse(reader.GetString(3));cursor=reader.GetString(0);
            players.Add(new{identity=cursor,name=reader.GetString(1),revision=reader.GetInt32(2),counters=doc.RootElement.Clone()});
        }
        return new{players,nextAfter=more?cursor:null,semantics="Latest cumulative statistics snapshot, not a sum of revisions. Missing optional counters remain absent. Restart pagination if the mission stats revision changes."};
    }

    public static async Task<object> Events(NpgsqlDataSource database,Guid server,Guid mission,int after,string? type,CancellationToken token)
    {
        if(after<0||type?.Length>32)throw new ArgumentException("Invalid event query.");
        await using var query=database.CreateCommand("""
            SELECT sequence,detail FROM gc.session_events WHERE server_id=$1 AND mission_id=$2 AND sequence>$3
                AND ($4::text IS NULL OR event_type=$4) ORDER BY sequence LIMIT 101
            """);
        query.Parameters.AddWithValue(server);query.Parameters.AddWithValue(mission);query.Parameters.AddWithValue(after);
        query.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Text,(object?)type??DBNull.Value);
        var events=new List<JsonElement>();var size=0;var cursor=after;var more=false;
        await using var reader=await query.ExecuteReaderAsync(token);
        while(await reader.ReadAsync(token))
        {
            var text=reader.GetString(1);var bytes=Encoding.UTF8.GetByteCount(text);
            if(bytes>2*1024*1024)throw new InvalidDataException("Event exceeds read limit; inspect its private source archive.");
            if(events.Count==100||size+bytes>2*1024*1024){more=true;break;}
            using var doc=JsonDocument.Parse(text);events.Add(doc.RootElement.Clone());size+=bytes;cursor=reader.GetInt32(0);
        }
        return new{events,nextAfterSequence=more?(int?)cursor:null,semantics="Recorded session events; pagination is stable only once projection is complete."};
    }
}
