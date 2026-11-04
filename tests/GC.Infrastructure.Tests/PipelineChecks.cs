using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GC.Infrastructure;
using Npgsql;

internal static class PipelineChecks
{
    public static async Task Run(HttpClient client,string connection,string archive,Guid server,string worker)
    {
        var profile=Path.Combine(Path.GetTempPath(),"gc-pipeline-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        foreach(var folder in OutboxDelivery.Folders.Values)Directory.CreateDirectory(Path.Combine(profile,folder));
        await File.WriteAllTextAsync(Path.Combine(profile,"Lobby2History","server-id.txt"),server.ToString());
        await using var database=Database.Open(connection);
        var mission=Guid.NewGuid();var recording=Guid.NewGuid();
        var batches=new List<byte[]>();
        for(var revision=11;revision>=0;revision--)
        {
            batches.Add(Stats(mission,revision));batches.Add(History(server,mission,revision));batches.Add(Session(mission,revision));
        }
        batches.Add(Telemetry(mission,recording));
        var source=Environment.GetEnvironmentVariable("GC_TEST_PROFILE_DIRECTORY");
        if(source is not null)
        {
            mission=Guid.Parse(Environment.GetEnvironmentVariable("GC_TEST_MISSION_ID")!);
            batches.Clear();
            foreach(var folder in OutboxDelivery.Folders.Values)
                foreach(var path in Directory.EnumerateFiles(Path.Combine(source,folder)))
                {
                    if(Path.GetExtension(path) is not(".json" or ".l2t"))continue;
                    var bytes=await File.ReadAllBytesAsync(path);using var batch=LobbyBatch.Read(bytes);
                    if(batch.MissionId!=mission)continue;
                    if(batch.Stream=="history"&&batch.Root.GetProperty("serverId").GetGuid()!=server)
                        throw new Exception("Real proof requires matching website server identity; set GC_TEST_SERVER_ID.");
                    if(batch.Stream=="telemetry")recording=batch.Id;
                    batches.Add(bytes);
                }
        }
        var originals=new Dictionary<string,string>();
        async Task Publish(byte[] bytes)
        {
            using var batch=LobbyBatch.Read(bytes);
            var path=Path.Combine(profile,OutboxDelivery.Folders[batch.Stream],batch.FileName);
            await File.WriteAllBytesAsync(path,bytes);await File.WriteAllTextAsync(path+".ready","ready");
            originals[path]=Convert.ToHexString(SHA256.HashData(bytes));
        }
        // Withhold a non-final statistics snapshot; latest state is usable but history coverage must remain incomplete.
        var held=batches.First(bytes=>{using var batch=LobbyBatch.Read(bytes);return batch.Stream=="stats"&&batch.Revision==0;});
        foreach(var bytes in batches)if(!ReferenceEquals(bytes,held))await Publish(bytes);
        var partial=Path.Combine(profile,"Lobby2Stats","unfinished.json");await File.WriteAllTextAsync(partial,"{");
        var malformed=Path.Combine(profile,"Lobby2Stats","broken.json");await File.WriteAllTextAsync(malformed,"{");await File.WriteAllTextAsync(malformed+".ready","ready");
        Process? process=null;Task<string>? output=null,errors=null;
        void Start()
        {
            var start=new ProcessStartInfo("dotnet"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            start.ArgumentList.Add(worker);start.Environment["ConnectionStrings__Gc"]=connection;
            start.Environment["Telemetry__Enabled"]="true";start.Environment["Missions__Enabled"]="true";start.Environment["Delivery__Enabled"]="true";
            start.Environment["Delivery__ProfilePath"]=profile;start.Environment["Ingest__ArchivePath"]=archive;start.Environment["Ingest__ServerId"]=server.ToString();
            process=Process.Start(start)!;output=process.StandardOutput.ReadToEndAsync();errors=process.StandardError.ReadToEndAsync();
        }
        async Task Stop()
        {
            if(process is null)return;if(!process.HasExited)process.Kill(true);await process.WaitForExitAsync();
            await Task.WhenAll(output!,errors!);process.Dispose();process=null;
        }
        async Task<long> Projected()
        {
            await using var query=database.CreateCommand("SELECT count(*) FROM gc.ingest_receipts WHERE server_id=$1 AND (recording_id=$2 OR recording_id=$3) AND status='projected'");
            query.Parameters.AddWithValue(server);query.Parameters.AddWithValue(mission);query.Parameters.AddWithValue(recording);
            return (long)(await query.ExecuteScalarAsync())!;
        }
        async Task Wait(Func<Task<bool>> predicate)
        {
            var deadline=DateTime.UtcNow.AddSeconds(120);
            while(DateTime.UtcNow<deadline)
            {
                if(await predicate())return;
                if(process?.HasExited==true)throw new Exception("Pipeline worker exited: "+await errors!);
                await Task.Delay(200);
            }
            await Stop(); throw new Exception("Pipeline condition timed out. " + await output! + await errors!);
        }
        async Task<JsonElement> Get(string route)
        {
            using var response=await client.GetAsync(route);response.EnsureSuccessStatusCode();
            using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync());return doc.RootElement.Clone();
        }
        try
        {
            // Same-host delivery refuses a profile/server mismatch before consuming any marker.
            try{await OutboxDelivery.Deliver(database,profile,archive,Guid.NewGuid(),default);throw new Exception("Wrong server accepted");}
            catch(InvalidDataException){}
            Start();await Wait(async()=>await Projected()>0);await Stop();Start();
            await Wait(async()=>await Projected()==batches.Count-1);
            var summary=await Get($"/api/v1/missions/{mission}");
            Check(!summary.GetProperty("streams").GetProperty("stats").GetProperty("allBatchesPresent").GetBoolean(),"Missing statistics revision is visible");
            await Publish(held);await Wait(async()=>await Projected()==batches.Count);
            summary=await Get($"/api/v1/missions/{mission}");
            foreach(var stream in new[]{"stats","session","history"})Check(summary.GetProperty("streams").GetProperty(stream).GetProperty("allBatchesPresent").GetBoolean(),"Complete stream revisions");
            byte[]? latestStatsBytes=null;var latestRevision=-1;
            foreach(var bytes in batches){using var batch=LobbyBatch.Read(bytes);if(batch.Stream=="stats"&&batch.Revision>latestRevision){latestRevision=batch.Revision;latestStatsBytes=bytes;}}
            var latestStats=LobbyBatch.Read(latestStatsBytes!);
            using(latestStats)
            {
                var expected=latestStats.Root.GetProperty("players").EnumerateArray().ToDictionary(p=>p.GetProperty("identity").GetString()!,p=>p.Clone());
                var actual=new Dictionary<string,JsonElement>();string? cursor=null;
                do
                {
                    var page=await Get($"/api/v1/missions/{mission}/players"+(cursor is null?"":"?after="+Uri.EscapeDataString(cursor)));
                    foreach(var player in page.GetProperty("players").EnumerateArray())actual.Add(player.GetProperty("identity").GetString()!,player.Clone());
                    cursor=page.GetProperty("nextAfter").GetString();
                }while(cursor is not null);
                Check(expected.Count==actual.Count,"Player snapshot count");
                foreach(var (id,player) in expected)
                    foreach(var counter in player.EnumerateObject().Where(p=>p.Value.ValueKind==JsonValueKind.Number))
                        Check(JsonElement.DeepEquals(counter.Value,actual[id].GetProperty("counters").GetProperty(counter.Name)),"Latest counters match source, no revision summation");
            }
            var expectedEvents=new Dictionary<int,JsonElement>();long telemetryRecords=0;
            foreach(var bytes in batches)
            {
                using var batch=LobbyBatch.Read(bytes);
                if(batch.Stream=="session")foreach(var entry in batch.Root.GetProperty("events").EnumerateArray())expectedEvents[entry.GetProperty("sequence").GetInt32()]=entry.Clone();
                if(batch.Stream=="telemetry")telemetryRecords+=batch.Root.GetProperty("records").GetArrayLength();
                if(batch.Stream=="history"&&batch.Revision==summary.GetProperty("streams").GetProperty("history").GetProperty("latestRevision").GetInt32())
                    Check(JsonElement.DeepEquals(batch.Root.GetProperty("sessionPerformance"),summary.GetProperty("streams").GetProperty("history").GetProperty("metadata").GetProperty("sessionPerformance")),"Performance matches source");
            }
            var gotEvents=new Dictionary<int,JsonElement>();int? sequence=0;
            do
            {
                var page=await Get($"/api/v1/missions/{mission}/events?afterSequence={sequence}");
                foreach(var entry in page.GetProperty("events").EnumerateArray())gotEvents.Add(entry.GetProperty("sequence").GetInt32(),entry.Clone());
                sequence=page.GetProperty("nextAfterSequence").ValueKind==JsonValueKind.Null?null:page.GetProperty("nextAfterSequence").GetInt32();
            }while(sequence is not null);
            Check(expectedEvents.Count==gotEvents.Count&&expectedEvents.All(e=>JsonElement.DeepEquals(e.Value,gotEvents[e.Key])),"Session events match source");
            Check(summary.GetProperty("recordingIds").EnumerateArray().Any(id=>id.GetGuid()==recording),"Mission links telemetry");
            var playback=await Get($"/api/v1/recordings/{recording}");Check(playback.GetProperty("recordCount").GetInt64()==telemetryRecords,"Telemetry joins same mission");
            await Stop();
            // Lost acknowledgement: marker restored after commit; delivery must not duplicate receipts/events.
            await Publish(held);await OutboxDelivery.Deliver(database,profile,archive,server,default);
            Check(await Projected()==batches.Count,"Duplicate delivery is idempotent");
            Check(File.Exists(malformed+".ready")&&File.Exists(partial),"Malformed and unpublished sources are retained");
            foreach(var (path,hash) in originals)Check(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)))==hash,"Source payload remains unchanged");
            var authorization=client.DefaultRequestHeaders.Authorization;client.DefaultRequestHeaders.Authorization=null;
            using(var denied=await client.GetAsync($"/api/v1/missions/{mission}"))Check(denied.StatusCode==HttpStatusCode.Unauthorized,"Mission API requires authorization");
            client.DefaultRequestHeaders.Authorization=authorization;
            await FailureProof(client,database,profile,archive,server);
            Console.WriteLine($"PASS: {(source is null?"synthetic":"copied engine")} four-stream pipeline: {batches.Count} batches, {expectedEvents.Count} session events, {telemetryRecords} telemetry records. Automatic delivery, restart, missing revision, duplicate acknowledgement, joined counters/performance/events, server identity and retained source bytes verified.");
        }
        finally
        {
            await Stop();
            if(Path.GetDirectoryName(profile)==Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)&&Path.GetFileName(profile).StartsWith("gc-pipeline-",StringComparison.Ordinal))Directory.Delete(profile,true);
        }
    }

    private static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
    private static async Task FailureProof(HttpClient client,NpgsqlDataSource database,string profile,string archive,Guid server)
    {
        var mission=Guid.NewGuid();var bytes=Stats(mission,0);using var batch=LobbyBatch.Read(bytes);
        var path=Path.Combine(profile,"Lobby2Stats",batch.FileName);await File.WriteAllBytesAsync(path,bytes);
        await File.WriteAllTextAsync(path+".ready","ready");
        var blockedArchive=Path.Combine(profile,"blocked-archive");await File.WriteAllTextAsync(blockedArchive,"occupied");
        await OutboxDelivery.Deliver(database,profile,blockedArchive,server,default);
        Check(File.Exists(path+".ready"),"Archive failure must not acknowledge source");
        await using(var retry=database.CreateCommand("UPDATE gc.delivery_failures SET next_attempt_at=now() WHERE server_id=$1"))
        {retry.Parameters.AddWithValue(server);await retry.ExecuteNonQueryAsync();}
        await OutboxDelivery.Deliver(database,profile,archive,server,default);
        Check(!File.Exists(path+".ready"),"Delivery resumes after storage recovery");
        // Simulate interruption after durable file publication but before receipt insertion.
        var orphanMission=Guid.NewGuid();var orphanBytes=Stats(orphanMission,0);
        var orphanHash=Convert.ToHexString(SHA256.HashData(orphanBytes));
        var orphanPath=Path.Combine(archive,$"{server:N}-stats-{orphanMission:N}-0-{orphanHash}.batch");
        await File.WriteAllBytesAsync(orphanPath,orphanBytes);
        Check(await Intake.Receive(database,archive,server,"stats",orphanMission,0,orphanBytes,default),"Orphan publication recovers on retry");
        Check((await File.ReadAllBytesAsync(orphanPath)).SequenceEqual(orphanBytes),"Recovery preserves durable bytes");
        while(await MissionProjection.ProjectNext(database,archive,default)){}
        // Large low-frequency JSON and additive metadata must not inherit the telemetry limit or poison the worker.
        var large=JsonNode.Parse(Stats(Guid.NewGuid(),0))!;large["extra"]=new string('x',1024*1024)+"\0";
        var largeBytes=Encoding.UTF8.GetBytes(large.ToJsonString());
        using(var response=await client.PostAsync($"/api/v1/ingest/stats/{large["missionId"]}/0",new ByteArrayContent(largeBytes)))
            Check(response.StatusCode==HttpStatusCode.Accepted,"Statistics larger than 1MiB are accepted");
        while(await MissionProjection.ProjectNext(database,archive,default)){}
        await using(var status=database.CreateCommand("SELECT status FROM gc.ingest_receipts WHERE server_id=$1 AND recording_id=$2"))
        {status.Parameters.AddWithValue(server);status.Parameters.AddWithValue(Guid.Parse(large["missionId"]!.ToString()));Check((string?)await status.ExecuteScalarAsync()=="projected","Large additive JSON projects");}
        // A late conflicting event rolls back earlier inserts from the same batch.
        var eventMission=Guid.NewGuid();var initial=JsonNode.Parse(Session(eventMission,1))!;initial["revision"]=0;
        var conflict=JsonNode.Parse(Session(eventMission,0))!;conflict["revision"]=1;
        var conflictingEvent=initial["events"]![0]!.DeepClone();conflictingEvent["actorName"]="different";
        conflict["events"]!.AsArray().Add(conflictingEvent);
        foreach(var payload in new[]{initial,conflict})
        {
            Check(await Intake.Receive(database,archive,server,"session",eventMission,payload["revision"]!.GetValue<int>(),Encoding.UTF8.GetBytes(payload.ToJsonString()),default),"Conflict fixture accepted by transport");
            while(await MissionProjection.ProjectNext(database,archive,default)){}
        }
        await using(var count=database.CreateCommand("SELECT count(*) FROM gc.session_events WHERE server_id=$1 AND mission_id=$2"))
        {count.Parameters.AddWithValue(server);count.Parameters.AddWithValue(eventMission);Check((long)(await count.ExecuteScalarAsync())! == 1,"Conflicting batch is atomic");}
        await using(var rejected=database.CreateCommand("SELECT status FROM gc.ingest_receipts WHERE server_id=$1 AND recording_id=$2 AND revision=1"))
        {rejected.Parameters.AddWithValue(server);rejected.Parameters.AddWithValue(eventMission);Check((string?)await rejected.ExecuteScalarAsync()=="rejected","Conflict is explicitly rejected");}
        var oversized=JsonNode.Parse(Session(Guid.NewGuid(),0))!;
        oversized["events"]![0]!["extra"]=new string('x',2*1024*1024);
        var oversizedId=Guid.Parse(oversized["sessionId"]!.ToString());
        await Intake.Receive(database,archive,server,"session",oversizedId,0,Encoding.UTF8.GetBytes(oversized.ToJsonString()),default);
        while(await MissionProjection.ProjectNext(database,archive,default)){}
        using(var response=await client.GetAsync($"/api/v1/missions/{oversizedId}/events"))
            Check(response.StatusCode==HttpStatusCode.UnprocessableEntity,"Oversized additive event is archived but cannot bypass read bound");
        Console.WriteLine("PASS: storage outage/recovery, orphan recovery, >1MiB statistics, additive metadata, bounded event reads and atomic session-conflict rejection");
    }
    internal static byte[] Stats(Guid mission,int revision)
    {
        var counters=new Dictionary<string,object>();foreach(var field in new[]{"shots","grenades","bandagesOthers","kills","deaths","teamKills","commanderMissions","leaderMissions","walkMetres","driveMetres","pilotMetres","radioSecondsClientReported"})counters[field]=0;
        counters["shots"]=revision;counters["playSeconds"]=revision+1;
        var assignment=new Dictionary<string,object>(counters){["faction"]="US",["groupId"]="group",["groupName"]="Group",["leader"]="player",["role"]="leader",["slotId"]=1,["fromSeconds"]=0,["untilSeconds"]=revision+1,["ammo"]=Array.Empty<object>()};
        var player=new Dictionary<string,object>(counters){["identity"]="player",["name"]="Test player",["assignments"]=new[]{assignment},["roles"]=Array.Empty<object>()};
        return JsonSerializer.SerializeToUtf8Bytes(new{schemaVersion=3,missionId=mission,world="test",startedAt=10,revision,final=revision==11,complete=true,durationSeconds=revision+1,playtimeBasis="connectedDuringGame",population=new[]{new{fromSeconds=0,players=1}},players=new[]{player},deaths=Array.Empty<object>()});
    }
    internal static byte[] History(Guid server,Guid mission,int revision)=>JsonSerializer.SerializeToUtf8Bytes(new{kind="missionHistory",schemaVersion=1,missionId=mission,serverId=server,revision,startedAt=0,endedAt=revision==11?100:0,observedSeconds=100,serverName="test",missionName="Pipeline proof",author="test",world="test",scenarioPath="test",description="",hostKind="workbench",status=revision==11?"completed":"running",outcome="unknown",endReason=0,closed=revision==11,complete=true,resultFactions=Array.Empty<string>(),winningFactions=Array.Empty<string>(),factions=new[]{new{key="US",name="US"}},phases=new[]{new{phase="GAME",sessionSeconds=10,at=10}},commanders=Array.Empty<object>(),sessionPerformance=Performance(),gamePerformance=Performance()});
    private static object Performance()=>new{samples=3,meanSampleFps=30,minimumSampleFps=10,maximumSampleFps=60,samplesBelow15Fps=1,peakSampledPlayers=1,peakSampledActiveAI=0,largestSampleGapSeconds=10};
    internal static byte[] Session(Guid mission,int revision)=>JsonSerializer.SerializeToUtf8Bytes(new{kind="sessionEvents",schemaVersion=1,sessionId=mission,revision,world="test",startedAt=0,droppedRecords=0,closed=revision==11,events=new[]{new{sequence=revision+1,sessionSeconds=revision+1,type=revision==0?"joined":revision==11?"left":"shot",actor="player",target="",actorName="Test player",targetName="",weapon="",projectile="",magazine="",detail="",reportedDamage=0,distanceMetres=0,damageType=-1}}});
    internal static byte[] Telemetry(Guid mission,Guid recording)=>TelemetryBinary.Encode(JsonSerializer.SerializeToElement(new{schemaVersion=2,kind="telemetry",missionId=mission,recordingId=recording,revision=0,startedAt=0,world="test",gameVersion="test",assetManifestId="test",clock="session-world-ms",coordinates="enfusion-world-metres",sampleHz=5,firstSequence=1,fromMs=100,untilMs=100,closed=true,stopReason="mission-end",droppedRecords=0,coverage=new{actors="players-and-ai",pose="inferred",shots=false,impacts=false,damage=false,grenades=false,vehicles=false,gear=false},records=new[]{new{sequence=1,timeMs=100,type="spawn",data=new{entity="entity",kind="ai",prefab="test",playerIdentity="",faction="US",position=new[]{0,0,0},anglesDegrees=new[]{0,0,0}}}}}));
}
