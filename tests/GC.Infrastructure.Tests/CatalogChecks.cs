using GC.Infrastructure;
using Npgsql;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GC.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Encodings.Web;
using System.Security.Claims;

internal static class CatalogChecks
{
 public static async Task Run()
 {
  MissionMetadataChecks.Run();
  var cs=Environment.GetEnvironmentVariable("GC_TEST_ADMIN_CONNECTION")??throw new Exception("Test connection required");
  var name="gc_test_"+Guid.NewGuid().ToString("N");var archive=Path.Combine(Path.GetTempPath(),name);Directory.CreateDirectory(archive);
  await using var admin=Database.Open(cs);await using(var q=admin.CreateCommand($"CREATE DATABASE {name}"))await q.ExecuteNonQueryAsync();
  try{
   await using var db=Database.Open(new NpgsqlConnectionStringBuilder(cs){Database=name,Pooling=false}.ConnectionString);await Database.Migrate(db);
   var parsed=CatalogSync.Parse("Missions/test.conf", "World \"{AAAAAAAAAAAAAAAA}World.ent\"\nm_sName \"COOP Test\"\nm_sDescription \"First\"\\\n \"\"\\\n \"Second\"", "{1111111111111111}", null,"test");
   Check(parsed.Description=="First\n\nSecond","Enfusion continued description preserves both paragraphs");
   foreach(var invalid in new[]{"[]","{\"tags\":[42]}","{\"tags\":{}}"})
   {try{CatalogSync.Parse("Missions/test.conf","World \"{AAAAAAAAAAAAAAAA}World.ent\"\nm_sName \"Test\"","{1111111111111111}",invalid,"test");throw new Exception("Bad sidecar accepted");}catch(InvalidDataException){}}
   Check(true,"Non-object sidecars and non-string tags rejected explicitly");
   using(var categories=JsonDocument.Parse("""{"missionType":"COOP","objectives":["Convoy","HVT","Sweep","Hostage","Delay","Search & Rescue","Defense","Attack & Defend"],"mods":["Lobby2"],"assetTypes":["Infantry","Helicopters"]}""")){
    var metadata=MissionCategories.Parse(categories.RootElement);
    Check(metadata.MissionType=="COOP"&&metadata.Objectives.Length==8&&metadata.Mods[0]=="Lobby2"&&metadata.AssetTypes.Length==2,"Categorized mission metadata retains objective, mod and asset tags");
    Check(metadata.SearchTags([]).Contains("Search & Rescue"),"Objective tags are searchable");
   }
   foreach(var invalid in new[]{"{\"missionType\":\"invalid\"}","{\"mods\":[42]}","{\"objectives\":\"Defense\"}"}){
    using var bad=JsonDocument.Parse(invalid);try{MissionCategories.Parse(bad.RootElement);throw new Exception("Invalid metadata accepted");}catch(InvalidDataException){}
   }
   using(var boundary=JsonDocument.Parse(JsonSerializer.Serialize(new{objectives=new[]{new string('a',80)}})))Check(MissionCategories.Parse(boundary.RootElement).Objectives[0].Length==80,"Category tag matches filter maximum");
   using(var overflow=JsonDocument.Parse(JsonSerializer.Serialize(new{objectives=new[]{new string('a',81)}}))){try{MissionCategories.Parse(overflow.RootElement);throw new Exception("Oversized category accepted");}catch(InvalidDataException){}}
   var handler=new Source();using var http=new HttpClient(handler);var sync=new CatalogSync(http);var source=new CatalogSource("Test/Missions","master");
   await sync.Sync(db,source,default);
   var list=JsonSerializer.SerializeToNode(await MissionCatalog.List(db,null,null,0,default))!;
   Check(list["missions"]!.AsArray().Count==2,"Both scenario definitions imported");
   var id=Guid.Parse(list["missions"]![0]!["id"]!.ToString());
   await MissionSelectionChecks.Run(db,id);
   var detail=JsonSerializer.SerializeToNode(await MissionCatalog.Detail(db,id,default))!;
   Check(detail["scenarioId"]!.ToString()=="{1111111111111111}Missions/A.conf","Actual scenario path wins over stale meta Name");
   Check(detail["tags"]!.AsArray().Any(t=>t!.ToString()=="Infantry"),"Authored tags imported");
   var votePage=JsonSerializer.SerializeToNode(await MissionVoting.Candidates(db,0,default))!;
   Check(votePage["version"]!.GetValue<int>()==1 && votePage["rows"]!.AsArray().Count==2,"Voting projects available scenario definitions");
   var voteRows=votePage["rows"]!.AsArray().Select(x=>JsonNode.Parse(x!.GetValue<string>())!).ToArray();
   Check(voteRows.Any(x=>x["scenario"]!.GetValue<string>()=="{1111111111111111}Missions/A.conf" && x["plays"]!.GetValue<string>()=="0" && x["lastPlayed"]!.GetValue<string>()=="Never played"),"Unplayed vote choices preserve exact scenario identity and unknown history");
   await using(var hide=db.CreateCommand("UPDATE gc.catalog_missions SET listed=false WHERE id=$1")){hide.Parameters.AddWithValue(id);await hide.ExecuteNonQueryAsync();}
   Check(JsonSerializer.SerializeToNode(await MissionVoting.Candidates(db,0,default))!["rows"]!.AsArray().Count==1,"Hidden missions cannot become vote choices");
   await using(var show=db.CreateCommand("UPDATE gc.catalog_missions SET listed=true WHERE id=$1")){show.Parameters.AddWithValue(id);await show.ExecuteNonQueryAsync();}
   try { await MissionVoting.Candidates(db,-1,default);throw new Exception("Invalid voting page accepted"); } catch(ArgumentException) {}
   var calls=handler.Calls;await sync.Sync(db,source,default);Check(handler.Calls==calls+1,"Unchanged commit needs only a ref check");
   handler.Version++;handler.GeneratedVersion=1;await sync.Sync(db,source,default);
   detail=JsonSerializer.SerializeToNode(await MissionCatalog.Detail(db,id,default))!;
   Check(detail["metadata"]!["generated"]!["entityCount"]!.GetValue<int>()==1,"Embedded native snapshot imported automatically from scenario header");
   handler.Version++;handler.GeneratedVersion=2;await sync.Sync(db,source,default);
   detail=JsonSerializer.SerializeToNode(await MissionCatalog.Detail(db,id,default))!;
   Check(detail["metadata"]!["generated"]!["entityCount"]!.GetValue<int>()==2,"Generated-only change refreshes cached mission");
   var server=Guid.NewGuid();var mission=Guid.NewGuid();var h=JsonNode.Parse(PipelineChecks.History(server,mission,0))!;
   h["scenarioResource"]="{1111111111111111}Missions/A.conf";h["world"]="{AAAAAAAAAAAAAAAA}World.ent";
   await Intake.Receive(db,archive,server,"history",mission,0,Encoding.UTF8.GetBytes(h.ToJsonString()),default);
   await Intake.Receive(db,archive,server,"stats",mission,0,PipelineChecks.Stats(mission,0),default);
   while(await MissionProjection.ProjectNext(db,archive,default)){}
   var sessions=JsonSerializer.SerializeToNode(await MissionCatalog.Sessions(db,id,null,0,default))!;
   Check(sessions["sessions"]!.AsArray().Count==1,"Resource identity links the played session");
   Check(JsonSerializer.SerializeToNode(await MissionCatalog.Sessions(db,id,Guid.NewGuid(),0,default))!["sessions"]!.AsArray().Count==0,"Server scope excludes other servers");
   var players=JsonSerializer.SerializeToNode(await Missions.Players(db,server,mission,null,default))!;
   Check(players["players"]![0]!["details"]!["assignments"]![0]!["faction"]!.ToString()=="US","Statistics retain team assignments");
   var secondServer=Guid.NewGuid();
   await using(var sample=db.CreateCommand("""
       INSERT INTO gc.mission_players(server_id,mission_id,revision,identity,name,counters)
       VALUES($1,$2,4,'player','Test player','{"kills":5,"aiKills":2}'),
             ($1,$2,4,'other','Test player','{"kills":19}')
       """)){sample.Parameters.AddWithValue(secondServer);sample.Parameters.AddWithValue(mission);await sample.ExecuteNonQueryAsync();}
   var profile=JsonSerializer.SerializeToNode(await GameRecords.Player(db,"player",default))!;
   Check((int)profile["sessions"]! == 2 && (int)profile["totals"]!["kills"]! == 5,"Career totals use the game identity, not the name");
   var history=JsonSerializer.SerializeToNode(await GameRecords.PlayerSessions(db,"player",0,default))!["sessions"]!.AsArray();
   Check(history.Count==2 && history.Any(x=>x!["catalogId"]?.GetValue<Guid>() == id && (Guid)x["serverId"]! == server),"Player history links the matched mission and server-specific session");
   var played=JsonSerializer.SerializeToNode(await GameRecords.Session(db,secondServer,mission,default))!;
   Check((int)played["recordedPlayers"]! == 2 && (int)played["totals"]!["kills"]! == 24 && played["catalogId"] is null,"Session totals include both players without borrowing another server's mission link");
   await using(var cleanup=db.CreateCommand("DELETE FROM gc.mission_players WHERE server_id=$1")){cleanup.Parameters.AddWithValue(secondServer);await cleanup.ExecuteNonQueryAsync();}
   await using(var q=db.CreateCommand("UPDATE gc.mission_players SET details=NULL"))await q.ExecuteNonQueryAsync();
   await MissionDetails.Backfill(db,archive,default);
   Check(JsonSerializer.SerializeToNode(await Missions.Players(db,server,mission,null,default))!["players"]![0]!["details"] is not null,"Existing archived statistics backfilled");
   handler.Version++;handler.Fail=true;
   try{await sync.Sync(db,source,default);throw new Exception("Failure accepted");}catch(InvalidOperationException){}
   Check(JsonSerializer.SerializeToNode(await MissionCatalog.Detail(db,id,default))!["available"]!.GetValue<bool>(),"Failed fetch preserves last good catalogue");
   handler.Fail=false;handler.RemoveB=true;await sync.Sync(db,source,default);
   list=JsonSerializer.SerializeToNode(await MissionCatalog.List(db,null,null,0,default))!;
   Check(list["missions"]!.AsArray().Count==2&&list["missions"]!.AsArray().Count(m=>m!["available"]!.GetValue<bool>())==1,"Removed definitions archived, not deleted");
   Check(JsonSerializer.SerializeToNode(await MissionCatalog.Sessions(db,id,null,0,default))!["sessions"]!.AsArray().Count==1,"History survives catalogue update");
   foreach(var broken in new[]{"{\"tags\":[42]}","[]","{broken"})
   {
    handler.Version++;handler.Sidecar=broken;await sync.Sync(db,source,default);
    var status=JsonSerializer.SerializeToNode(await MissionCatalog.Status(db,default))!;
    Check(status["sources"]![0]!["issues"]!.AsArray().Count==1&&status["sources"]![0]!["issues"]![0]!["retained"]!.GetValue<bool>(),"Invalid sidecar quarantined with previous definition retained");
   }
   await Api(db,id,server,archive);
   await using(var q=db.CreateCommand("UPDATE gc.mission_batches SET header=(header::jsonb - 'scenarioResource')::text WHERE stream='history'"))await q.ExecuteNonQueryAsync();
   Check(JsonSerializer.SerializeToNode(await MissionCatalog.Sessions(db,id,null,0,default))!["sessions"]!.AsArray().Count==0,"Shared world GUID stays ambiguous even with archived definitions");
   var manifest=JsonSerializer.Serialize(new{key="arland-lobby",name="Arland Lobby",map="Arland",author="",description="Test",world="{2DEA56A8DD9330F2}worlds/examples/arland/Arland_Lobby.ent",worldAliases=new[]{"$Lobby2:worlds/examples/arland/Arland_Lobby.ent"},tags=new[]{"Testing","Lobby2"}});
   var local=await LocalMissionImport.Import(db,manifest,true);
   Check(await LocalMissionImport.Import(db,manifest,true)==local,"Repeated local import keeps mission identity");
   list=JsonSerializer.SerializeToNode(await MissionCatalog.List(db,null,null,0,default))!;
   Check(list["missions"]!.AsArray().Count==1 && list["missions"]![0]!["scenarioId"] is null,"Test list replaces visible catalog without inventing launch scenario");
   Check(await MissionCatalog.Detail(db,id,default) is not null,"Hidden definitions and history retained");
   await using(var q=db.CreateCommand("UPDATE gc.mission_batches SET world='$Lobby2:worlds/examples/arland/Arland_Lobby.ent' WHERE stream='history'"))await q.ExecuteNonQueryAsync();
   Check(JsonSerializer.SerializeToNode(await MissionCatalog.Sessions(db,local,null,0,default))!["sessions"]!.AsArray().Count==1,"Exact authored world alias links historical session");
   var stats=JsonSerializer.SerializeToNode(await MissionCatalog.Statistics(db,local,null,default))!;
   Check(stats["sessions"]!.GetValue<long>()==1 && stats["players"]!.GetValue<long>()==1,"Mission aggregates use linked sessions and players");
   Check(JsonSerializer.SerializeToNode(await MissionCatalog.Statistics(db,local,Guid.NewGuid(),default))!["sessions"]!.GetValue<long>()==0,"Mission statistics respect server scope");
  }finally{await using var drop=admin.CreateCommand($"DROP DATABASE {name} WITH (FORCE)");await drop.ExecuteNonQueryAsync();Directory.Delete(archive,true);}
 }
 static void Check(bool ok,string message){if(!ok)throw new Exception(message);Console.WriteLine("PASS: "+message);}
 static async Task Api(NpgsqlDataSource db,Guid id,Guid server,string archive)
 {
  var builder=WebApplication.CreateBuilder(new WebApplicationOptions{EnvironmentName="Development",WebRootPath=Path.GetFullPath("dist")});builder.Logging.ClearProviders();
  builder.Services.AddSingleton(db);builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions,Member>("test",_=>{});
  builder.Services.AddAuthorization(o=>o.AddPolicy(SiteNavigation.AdminPolicy,p=>p.RequireRole("Developer")));
  builder.Services.AddRateLimiter(o=>o.AddPolicy("intake",_=>System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter("test")));
  builder.Configuration["Ingest:Enabled"]="true";builder.Configuration["Ingest:ApiKey"]=new string('x',40);builder.Configuration["Ingest:ServerId"]=server.ToString();builder.Configuration["Ingest:ArchivePath"]=archive;
  await using var app=builder.Build();app.UseAuthentication();app.UseAuthorization();app.UseRateLimiter();CatalogRoutes.Map(app);Integrations.Map(app);AdminData.Map(app);
  app.Urls.Add("http://127.0.0.1:0");await app.StartAsync();using var client=new HttpClient{BaseAddress=new Uri(app.Urls.Single())};
  Check((await client.GetAsync("/api/catalog")).StatusCode==HttpStatusCode.OK,"Public catalogue available");
  Check((await client.GetAsync($"/api/admin/catalog/{id}/sessions")).StatusCode==HttpStatusCode.Unauthorized,"Session history requires authentication");
  Check((await client.GetAsync("/api/admin/game/player?identity=player")).StatusCode==HttpStatusCode.Unauthorized,"Player profile requires authentication");
  client.DefaultRequestHeaders.Add("X-Test-Role","Member");Check((await client.GetAsync($"/api/admin/catalog/{id}/sessions")).StatusCode==HttpStatusCode.Forbidden,"Member cannot read restricted history");
  Check((await client.GetAsync("/api/admin/game/sessions")).StatusCode==HttpStatusCode.Forbidden,"Session directory keeps the history permission");
  client.DefaultRequestHeaders.Remove("X-Test-Role");client.DefaultRequestHeaders.Add("X-Test-Role","Developer");Check((await client.GetAsync($"/api/admin/catalog/{id}/sessions")).StatusCode==HttpStatusCode.OK,"Developer can read history");
  Check((await client.GetAsync("/api/admin/game/sessions?sort=map&direction=asc&search=Test")).IsSuccessStatusCode,"Session filters accepted");
  Check((await client.GetAsync("/api/admin/game/sessions?sort=invalid")).StatusCode==HttpStatusCode.BadRequest,"Invalid session sort rejected");
  Check((await client.GetAsync("/api/admin/game/sessions?direction=invalid")).StatusCode==HttpStatusCode.BadRequest,"Invalid session direction rejected");
  Check((await client.GetAsync("/api/admin/game/players?search=Test")).IsSuccessStatusCode && (await client.GetAsync("/api/admin/game/player/sessions?identity=player")).IsSuccessStatusCode,"Developer can browse players and drill into session performance");
  Check((await client.GetAsync("/api/v1/catalog")).StatusCode==HttpStatusCode.Unauthorized,"Website role does not grant machine API access");
  Check((await client.GetAsync("/api/v1/catalog/voting")).StatusCode==HttpStatusCode.Unauthorized,"Website roles cannot access mission voting API");
  Check((await client.PostAsync("/api/v1/catalog/selection/query",new StringContent("{\"search\":\"\",\"offset\":0}",Encoding.UTF8,"application/json"))).StatusCode==HttpStatusCode.Unauthorized,"Website admin role cannot access game mission selection");
  Check((await client.PostAsync("/api/v1/catalog/selection/launch",new StringContent(JsonSerializer.Serialize(new MissionSelection.Launch(Guid.NewGuid(),id,"fixture")),Encoding.UTF8,"application/json"))).StatusCode==HttpStatusCode.Unauthorized,"Website admin role cannot queue game launches");
  client.DefaultRequestHeaders.Authorization=new("Bearer",new string('x',40));Check((await client.GetAsync("/api/v1/catalog")).StatusCode==HttpStatusCode.OK,"Server credential reads catalogue");
  Check((await client.GetAsync("/api/v1/catalog/voting")).StatusCode==HttpStatusCode.OK,"Server credential reads voting projection");
  Check((await client.PostAsync("/api/v1/catalog/selection/query",new StringContent("{\"search\":\"\",\"offset\":0}",Encoding.UTF8,"application/json"))).StatusCode==HttpStatusCode.OK,"Machine credential reads admin mission selection");
  Check((await client.GetAsync("/api/v1/catalog/voting?offset=-1")).StatusCode==HttpStatusCode.BadRequest,"Invalid voting pagination rejected");
  Check((await client.GetAsync($"/api/v1/catalog/{id}/sessions")).StatusCode==HttpStatusCode.OK,"Server credential reads scoped history");
  Check((await client.GetAsync("/api/catalog?offset=-1")).StatusCode==HttpStatusCode.BadRequest,"Invalid pagination rejected");
 }
 sealed class Member(IOptionsMonitor<AuthenticationSchemeOptions> o,ILoggerFactory l,UrlEncoder e):AuthenticationHandler<AuthenticationSchemeOptions>(o,l,e)
 {protected override Task<AuthenticateResult> HandleAuthenticateAsync(){var role=Request.Headers["X-Test-Role"].ToString();return Task.FromResult(role.Length==0?AuthenticateResult.NoResult():AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name,"Fixture"),new Claim(ClaimTypes.Role,role)],"test")),"test")));}}
 sealed class Source:HttpMessageHandler
 {
  public int Calls,Version=1,GeneratedVersion;public bool Fail,RemoveB;public string? Sidecar;
  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)
  {
   Calls++;var path=r.RequestUri!.AbsolutePath;object data;
   if(path.Contains("/git/ref/"))data=new{ @object=new{sha=Version.ToString("x40")}};
   else if(path.Contains("/git/trees/")){
    var files=new List<object>();foreach(var n in RemoveB?new[]{"A"}:new[]{"A","B"})foreach(var suffix in new[]{".conf",".conf.meta",".conf.metadata.json"})files.Add(new{path="Missions/"+n+suffix,type="blob",sha=n+suffix+(suffix==".conf.metadata.json"?Sidecar:suffix==".conf"?GeneratedVersion.ToString():"")});
    data=new{truncated=false,tree=files};
   }else{
    if(Fail)return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    var a=path.Contains("/A.conf");var text=path.EndsWith(".meta")?$"Meta {{ Name \"{{{(a?"1111111111111111":"2222222222222222")}}}Missions/Old.conf\" }}":path.EndsWith(".json")?"{\"tags\":[\"Infantry\"]}":$"SCR_MissionHeader {{\n World \"{{AAAAAAAAAAAAAAAA}}World.ent\"\n m_sName \"COOP {(a?"Alpha":"Bravo")}\"\n m_sAuthor \"Creator\"\n m_sDescription \"Description\"\n m_iPlayerCount 32\n}}";
    if(path.EndsWith(".conf")&&GeneratedVersion>0)text=text.TrimEnd()[..^1]+$"\n m_GC_GeneratedMetadata GC_GeneratedMissionMetadata {{\n m_sWorld \"{{AAAAAAAAAAAAAAAA}}World.ent\"\n m_sCaptureScope \"world_subscene\"\n m_iEntityCount {GeneratedVersion}\n }}\n}}";
    if(path.EndsWith(".json")&&Sidecar is not null)text=Sidecar;
    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(text)});
   }
   return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(data))});
  }
 }
}
