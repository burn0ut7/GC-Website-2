using GC.Web;
using GC.Infrastructure;
using Npgsql;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;

internal static class ExplorerChecks
{
 public static async Task Run()
 {
  var connection=Environment.GetEnvironmentVariable("GC_TEST_ADMIN_CONNECTION")??throw new Exception("Test administrator connection required");
  var name="gc_test_"+Guid.NewGuid().ToString("N");var archive=Path.Combine(Path.GetTempPath(),name);Directory.CreateDirectory(archive);
  await using var admin=Database.Open(connection);
  await using(var q=admin.CreateCommand($"CREATE DATABASE {name}"))await q.ExecuteNonQueryAsync();
  try
  {
   var cs=new NpgsqlConnectionStringBuilder(connection){Database=name,Pooling=false};await using var db=Database.Open(cs.ConnectionString);await Database.Migrate(db);
   var builder=WebApplication.CreateBuilder(new WebApplicationOptions { WebRootPath=Path.GetFullPath("dist") });builder.Logging.ClearProviders();builder.Configuration["Ingest:ArchivePath"]=archive;
   builder.Services.AddSingleton(db);builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions,Member>("test",_=>{});
   builder.Services.AddAuthorization(o=>o.AddPolicy(SiteNavigation.AdminPolicy,p=>p.RequireRole("Developer")));
   builder.Services.AddAntiforgery(o=>o.HeaderName="X-CSRF-TOKEN");
   await using var app=builder.Build();app.UseAuthentication();app.UseAuthorization();
   DataExplorer.Map(app,app.MapGroup("/api/admin").RequireAuthorization(SiteNavigation.AdminPolicy));
   app.MapGet("/csrf",(Microsoft.AspNetCore.Http.HttpContext h,IAntiforgery a)=>new{token=a.GetAndStoreTokens(h).RequestToken});
   app.Urls.Add("http://127.0.0.1:0");await app.StartAsync();
   using var client=new HttpClient(new HttpClientHandler{CookieContainer=new CookieContainer()}){BaseAddress=new Uri(app.Urls.Single())};
   Check((await client.GetAsync("/api/admin/explorer?stream=stats")).StatusCode==HttpStatusCode.Unauthorized,"Anonymous denied");
   client.DefaultRequestHeaders.Add("X-Test-Role","Member");Check((await client.GetAsync("/api/admin/explorer?stream=stats")).StatusCode==HttpStatusCode.Forbidden,"Member denied");
   client.DefaultRequestHeaders.Remove("X-Test-Role");client.DefaultRequestHeaders.Add("X-Test-Role","Developer");
   var server=Guid.NewGuid();var mission=Guid.NewGuid();var recording=Guid.NewGuid();
   Check((await client.PostAsync($"/api/admin/explorer/import/{server}",new ByteArrayContent(PipelineChecks.Stats(mission,0)))).StatusCode==HttpStatusCode.BadRequest,"Import requires CSRF");
   var csrf=await client.GetFromJsonAsync<JsonObject>("/csrf");client.DefaultRequestHeaders.Add("X-CSRF-TOKEN",(string)csrf!["token"]!);
   foreach(var bytes in new[]{PipelineChecks.Stats(mission,0),PipelineChecks.Session(mission,0),PipelineChecks.History(server,mission,0),VoiceChecks.Batch(mission,recording)})
    {using var result=await client.PostAsync($"/api/admin/explorer/import/{server}",new ByteArrayContent(bytes));Check(result.StatusCode==HttpStatusCode.Accepted,"Valid stream imported through intake: "+result.StatusCode+" "+await result.Content.ReadAsStringAsync());}
   Check((await client.PostAsync($"/api/admin/explorer/import/{server}",new ByteArrayContent("{}"u8.ToArray()))).StatusCode==HttpStatusCode.BadRequest,"Invalid file rejected");
   Check((await client.PostAsync($"/api/admin/explorer/import/{Guid.NewGuid()}",new ByteArrayContent(PipelineChecks.History(server,mission,0)))).StatusCode==HttpStatusCode.BadRequest,"Wrong history server rejected");
   while(await MissionProjection.ProjectNext(db,archive,default)){}while(await TelemetryProjection.ProjectNext(db,archive,default)){}
   foreach(var stream in new[]{"stats","session","history","telemetry"}){var list=await client.GetFromJsonAsync<JsonObject>($"/api/admin/explorer?stream={stream}");Check(list!["rows"]!.AsArray().Count==1,"Dataset listed: "+stream);}
   var servers=await client.GetFromJsonAsync<JsonArray>("/api/admin/explorer/servers");Check(servers!.Any(s=>(string?)s!["id"]==server.ToString()),"Import picker discovers received server");
   var route=$"/api/admin/explorer/{server}/stats/{mission}";
   var downloaded=await client.GetByteArrayAsync(route+"/batches/0");Check(downloaded.SequenceEqual(PipelineChecks.Stats(mission,0)),"Download preserves original bytes");
   Check((await client.DeleteAsync(route+"?expectedBatches=2")).StatusCode==HttpStatusCode.Conflict,"Changed dataset count blocks removal");
   client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");Check((await client.DeleteAsync(route+"?expectedBatches=1")).StatusCode==HttpStatusCode.BadRequest,"Removal requires CSRF");client.DefaultRequestHeaders.Add("X-CSRF-TOKEN",(string)csrf["token"]!);
   Check((await client.DeleteAsync(route+"?expectedBatches=1")).IsSuccessStatusCode,"Removes selected statistics dataset");
   await using(var q=db.CreateCommand("SELECT (SELECT count(*) FROM gc.mission_players),(SELECT count(*) FROM gc.session_events),(SELECT count(*) FROM gc.telemetry_batches),(SELECT count(*) FROM gc.mission_batches WHERE stream='history')")){await using var r=await q.ExecuteReaderAsync();await r.ReadAsync();Check(r.GetInt64(0)==0&&r.GetInt64(1)==1&&r.GetInt64(2)==1&&r.GetInt64(3)==1,"Removal is stream-scoped and preserves other data");}
   Check(Directory.GetFiles(archive,"*.batch").Length==4,"Source archives retained");
   var filtered=System.Text.Json.JsonSerializer.SerializeToNode(await Recordings.Range(db,archive,server,recording,0,1000,0,100,default,"not-present"));Check(filtered!["records"]!.AsArray().Count==0,"Telemetry type filter excludes nonmatching records");
   var voices=System.Text.Json.JsonSerializer.SerializeToNode(await Recordings.Range(db,archive,server,recording,0,1000,0,100,default,"voice-start"));Check(voices!["records"]!.AsArray().Count==1,"Imported voice survives projection and filtered playback retrieval");
   Check((await client.PostAsync($"/api/admin/explorer/import/{server}",new ByteArrayContent(downloaded))).StatusCode==HttpStatusCode.Accepted,"Downloaded source can be reimported after removal");
   while(await MissionProjection.ProjectNext(db,archive,default)){}
   await app.StopAsync();
  }
  finally{await using var q=admin.CreateCommand($"DROP DATABASE {name} WITH (FORCE)");await q.ExecuteNonQueryAsync();Directory.Delete(archive,true);}
 }
 static void Check(bool ok,string message){if(!ok)throw new Exception(message);Console.WriteLine("PASS: "+message);}
 sealed class Member(IOptionsMonitor<AuthenticationSchemeOptions> o,ILoggerFactory l,UrlEncoder e):AuthenticationHandler<AuthenticationSchemeOptions>(o,l,e)
 {protected override Task<AuthenticateResult> HandleAuthenticateAsync(){var role=Request.Headers["X-Test-Role"].ToString();return Task.FromResult(role.Length==0?AuthenticateResult.NoResult():AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name,"Fixture"),new Claim(ClaimTypes.Role,role)],"test")),"test")));}}
}
