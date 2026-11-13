using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GC.Infrastructure;
using Npgsql;

internal static class SessionDirectoryChecks
{
    public static async Task Run()
    {
        var cs=Environment.GetEnvironmentVariable("GC_TEST_ADMIN_CONNECTION")??throw new Exception("Test connection required");
        var name="gc_test_"+Guid.NewGuid().ToString("N");
        var archive=Path.Combine(Path.GetTempPath(),name);
        await using var admin=Database.Open(cs);
        await using(var create=admin.CreateCommand($"CREATE DATABASE {name}"))await create.ExecuteNonQueryAsync();
        try
        {
            await using var db=Database.Open(new NpgsqlConnectionStringBuilder(cs){Database=name,Pooling=false}.ConnectionString);
            await Database.Migrate(db);
            var server=Guid.NewGuid();var identity=Guid.NewGuid();var mine=Guid.NewGuid();
            var user=await Accounts.SignInDiscord(db,"123456789012345678","Player",default);
            await SteamAccounts.Link(db,user,"76561198000000001",default);
            for(var i=0;i<52;i++)
            {
                var mission=i==10?mine:Guid.NewGuid();
                await Intake.Receive(db,archive,server,"history",mission,0,PipelineChecks.History(server,mission,0),default);
                while(await MissionProjection.ProjectNext(db,archive,default)){}
                await using var seed=db.CreateCommand("UPDATE gc.mission_batches SET started_at=$3,header=(header::jsonb || $4::jsonb)::text WHERE server_id=$1 AND mission_id=$2");
                seed.Parameters.AddWithValue(server);seed.Parameters.AddWithValue(mission);seed.Parameters.AddWithValue((long)(1000+i));
                seed.Parameters.AddWithValue(JsonSerializer.Serialize(new{startedAt=1000+i,missionName=$"Mission {i:D2}",world=i%2==0?"$Test:worlds/arland_variant.ent":"{1234567890ABCDEF}worlds/everon_playback.ent",outcome=i%2==0?"draw":"defeat",observedSeconds=100+i}));
                await seed.ExecuteNonQueryAsync();
            }
            var stats=JsonNode.Parse(PipelineChecks.Stats(mine,0))!;
            stats["startedAt"]=1010;stats["players"]![0]!["identity"]=identity.ToString();stats["players"]![0]!["steamId"]="76561198000000001";
            await Intake.Receive(db,archive,server,"stats",mine,0,Encoding.UTF8.GetBytes(stats.ToJsonString()),default);
            while(await MissionProjection.ProjectNext(db,archive,default)){}
            async Task<JsonNode> Page(string sort="time",bool ascending=false,string search="",Guid? owner=null,int offset=0)=>
                JsonSerializer.SerializeToNode(await MissionCatalog.Sessions(db,null,null,offset,default,search,sort,ascending,owner,[new("Arland","worlds/arland_variant.ent"),new("Everon","worlds/everon_playback.ent")]))!;
            var newest=await Page();var oldest=await Page(ascending:true);
            Check(newest["sessions"]![0]!["startedAt"]!.GetValue<long>()==1051 && oldest["sessions"]![0]!["startedAt"]!.GetValue<long>()==1000,"Time sorting covers full history");
            Check(newest["nextOffset"]!.GetValue<int>()==50 && (await Page(offset:50))["sessions"]!.AsArray().Count==2,"Sorting retains pagination");
            foreach(var sort in new[]{"mission","map","outcome","duration"})
            {
                var up=(await Page(sort,true))["sessions"]!.AsArray();var down=(await Page(sort,false))["sessions"]!.AsArray();
                Check(up[0]!["missionId"]!.GetValue<Guid>()!=down[0]!["missionId"]!.GetValue<Guid>(),sort+" supports both directions");
            }
            Check(newest["sessions"]![0]!["map"]!.GetValue<string>()=="Everon","World variants display their registered base map name");
            var unmapped=JsonSerializer.SerializeToNode(await MissionCatalog.Sessions(db,null,null,0,default))!;
            Check(unmapped["sessions"]![0]!["map"] is null,"Unknown base map does not expose a world filename");
            Check((await Page(search:"Arland"))["sessions"]!.AsArray().Count==26,"Map filter applies before pagination");
            Check((await Page(search:"Mission 51"))["sessions"]!.AsArray().Count==1,"Mission search includes older pages");
            Check((await Page(search:"defeat"))["sessions"]!.AsArray().Count==26,"Outcome filtering");
            Check((await Page(owner:user))["sessions"]!.AsArray().Single()!["missionId"]!.GetValue<Guid>()==mine,"My sessions uses verified participant identity");
            Check((await Page(owner:Guid.NewGuid()))["sessions"]!.AsArray().Count==0,"Other accounts cannot borrow participation");
            await using(var connection=await db.OpenConnectionAsync())await using(var tx=await connection.BeginTransactionAsync())
            {await SteamAccounts.Observe(connection,tx,server,mine,identity.ToString(),"76561198000000002",default);await tx.CommitAsync();}
            Check((await Page(owner:user))["sessions"]!.AsArray().Count==0,"Conflicting identity cannot match my sessions");
        }
        finally
        {
            await using var drop=admin.CreateCommand($"DROP DATABASE {name} WITH (FORCE)");await drop.ExecuteNonQueryAsync();
            if(Directory.Exists(archive))Directory.Delete(archive,true);
        }
    }
    private static void Check(bool value,string message){if(!value)throw new Exception(message);Console.WriteLine("PASS: "+message);}
}



