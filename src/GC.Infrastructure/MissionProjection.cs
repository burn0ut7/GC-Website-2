using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace GC.Infrastructure;

public static class MissionProjection
{
    public static async Task<bool> ProjectNext(NpgsqlDataSource database,string archive,CancellationToken token)
    {
        await using var work=await ProjectionWork.Next(database,["stats","session","history"],token);
        if(work is null)return false;
        var bytes=await work.Read(archive,token);if(bytes is null)return true;
        LobbyBatch batch;
        try{batch=LobbyBatch.Read(bytes);}
        catch(Exception error)when(LobbyBatch.IsInvalid(error)){await work.Finish("rejected","invalid_batch",token);return true;}
        using(batch)
        {
            if(batch.Stream!=work.Stream||batch.Id!=work.Recording||batch.Revision!=work.Revision||
                (batch.Stream=="history"&&batch.Root.GetProperty("serverId").GetGuid()!=work.Server))
            {await work.Finish("rejected","envelope_mismatch",token);return true;}
            await using(var gate=Command(work,"SELECT pg_advisory_xact_lock(hashtextextended($1,0))",$"mission:{work.Server}:{work.Recording}"))
                await gate.ExecuteNonQueryAsync(token);
            await work.Transaction.SaveAsync("projection",token);
            try
            {
                await Project(work,batch,token);
                await work.Finish("projected",null,token);
            }
            catch(InvalidDataException)
            {
                await work.Transaction.RollbackAsync("projection",token);
                await work.Finish("rejected","continuity_conflict",token);
            }
        }
        return true;
    }

    private static async Task Project(ProjectionWork work,LobbyBatch batch,CancellationToken token)
    {
        var root=batch.Root;
        var world=root.GetProperty("world").GetString()!;
        var started=root.GetProperty("startedAt").GetInt64();
        var finished=root.GetProperty(batch.Stream=="stats"?"final":"closed").GetBoolean();
        var complete=batch.Stream!="session"&&root.GetProperty("complete").GetBoolean();
        var drops=batch.Stream=="session"?root.GetProperty("droppedRecords").GetInt32():0;
        await using(var peers=Command(work,"""
            SELECT revision,world,started_at,finished FROM gc.mission_batches
            WHERE server_id=$1 AND mission_id=$2 AND stream=$3
            """,work.Server,batch.Id,batch.Stream))
        {
            await using var reader=await peers.ExecuteReaderAsync(token);
            while(await reader.ReadAsync(token))
            {
                var revision=reader.GetInt32(0);
                if(reader.GetString(1)!=world||reader.GetInt64(2)!=started||
                    (revision<batch.Revision&&reader.GetBoolean(3))||(revision>batch.Revision&&finished))
                    throw new InvalidDataException("Mission metadata or closure conflict.");
            }
        }
        int? first=null,last=null;double? from=null,until=null;
        if(batch.Stream=="session")
        {
            foreach(var entry in root.GetProperty("events").EnumerateArray())
            {
                var sequence=entry.GetProperty("sequence").GetInt32();
                var seconds=entry.GetProperty("sessionSeconds").GetDouble();
                first??=sequence;from??=seconds;last=sequence;until=seconds;
                await using(var existing=Command(work,"SELECT detail FROM gc.session_events WHERE server_id=$1 AND mission_id=$2 AND sequence=$3",work.Server,batch.Id,sequence))
                {
                    if(await existing.ExecuteScalarAsync(token) is string detail)
                    {
                        using var previous=JsonDocument.Parse(detail);
                        if(!JsonElement.DeepEquals(previous.RootElement,entry))throw new InvalidDataException("Conflicting event.");
                        continue;
                    }
                }
                await using var insert=Command(work,"""
                    INSERT INTO gc.session_events(server_id,mission_id,sequence,session_seconds,event_type,actor,target,detail)
                    VALUES($1,$2,$3,$4,$5,$6,$7,$8)
                    """,work.Server,batch.Id,sequence,seconds,entry.GetProperty("type").GetString()!,
                    entry.GetProperty("actor").GetString()!,entry.GetProperty("target").GetString()!,entry.GetRawText());
                await insert.ExecuteNonQueryAsync(token);
            }
            // Sequence order must also be chronological across independently arriving batches.
            await using var order=Command(work,"""
                SELECT EXISTS(SELECT 1 FROM (
                    SELECT session_seconds,lag(session_seconds) OVER(ORDER BY sequence) AS previous
                    FROM gc.session_events WHERE server_id=$1 AND mission_id=$2) e WHERE session_seconds<previous)
                """,work.Server,batch.Id);
            if(await order.ExecuteScalarAsync(token) is true)throw new InvalidDataException("Session time moved backwards.");
        }
        if(batch.Stream=="stats")
        {
            await using var latest=Command(work,"SELECT max(revision) FROM gc.mission_batches WHERE server_id=$1 AND mission_id=$2 AND stream='stats'",work.Server,batch.Id);
            var latestRevision=await latest.ExecuteScalarAsync(token);
            if(latestRevision is not int previous||batch.Revision>previous)
            {
                await using(var remove=Command(work,"DELETE FROM gc.mission_players WHERE server_id=$1 AND mission_id=$2",work.Server,batch.Id))await remove.ExecuteNonQueryAsync(token);
                foreach(var player in root.GetProperty("players").EnumerateArray())
                {
                    // Optional counters remain absent, not invented zeroes; detailed assignments stay in the source archive.
                    var counterNames=new[]{"playSeconds","walkMetres","driveMetres","pilotMetres","radioSecondsClientReported","shots","grenades","bandagesOthers","kills","deaths","commanderMissions","leaderMissions","teamKills","aiKills","deathsByPlayers","deathsByAI"};
                    var counters=JsonSerializer.Serialize(player.EnumerateObject().Where(p=>counterNames.Contains(p.Name,StringComparer.Ordinal))
                        .ToDictionary(p=>p.Name,p=>p.Value));
                    await using var insert=Command(work,"INSERT INTO gc.mission_players(server_id,mission_id,revision,identity,name,counters) VALUES($1,$2,$3,$4,$5,$6)",
                        work.Server,batch.Id,batch.Revision,player.GetProperty("identity").GetString()!,player.GetProperty("name").GetString()!,counters);
                    await insert.ExecuteNonQueryAsync(token);
                }
            }
        }
        var header=JsonSerializer.Serialize(root.EnumerateObject().Where(p=>p.Name is not("players" or "events" or "deaths"))
            .ToDictionary(p=>p.Name,p=>p.Value));
        await using var metadata=Command(work,"""
            INSERT INTO gc.mission_batches(server_id,mission_id,stream,revision,header,world,started_at,finished,source_complete,dropped_records,
                first_sequence,last_sequence,first_seconds,last_seconds)
            VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14)
            """,work.Server,batch.Id,batch.Stream,batch.Revision,header,world,started,finished,complete,drops);
        metadata.Parameters.AddWithValue(NpgsqlDbType.Integer,(object?)first??DBNull.Value);
        metadata.Parameters.AddWithValue(NpgsqlDbType.Integer,(object?)last??DBNull.Value);
        metadata.Parameters.AddWithValue(NpgsqlDbType.Double,(object?)from??DBNull.Value);
        metadata.Parameters.AddWithValue(NpgsqlDbType.Double,(object?)until??DBNull.Value);
        await metadata.ExecuteNonQueryAsync(token);
    }

    private static NpgsqlCommand Command(ProjectionWork work,string sql,params object[] values)
    {
        var command=new NpgsqlCommand(sql,work.Connection,work.Transaction);
        foreach(var value in values)command.Parameters.AddWithValue(value);
        return command;
    }
}
