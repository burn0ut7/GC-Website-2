using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GC.Infrastructure;
using GC.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

// Explicit loopback fixture using the production importer, machine authentication and queue.
internal static class LaunchLive
{
    public static async Task Run(string header, string output)
    {
        Directory.CreateDirectory(output);
        var connection = Environment.GetEnvironmentVariable("GC_TEST_ADMIN_CONNECTION") ?? throw new Exception("Test connection required");
        var name = "gc_test_launch_" + Guid.NewGuid().ToString("N");
        await using var admin = Database.Open(connection);
        await using (var create = admin.CreateCommand($"CREATE DATABASE {name}")) await create.ExecuteNonQueryAsync();
        try
        {
            await using var db = Database.Open(new NpgsqlConnectionStringBuilder(connection) { Database = name, Pooling = false }.ConnectionString);
            await Database.Migrate(db);
            var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(header)));
            var mission = CatalogSync.Parse("Configs/Missions/" + Path.GetFileName(header), await File.ReadAllTextAsync(header), await File.ReadAllTextAsync(header + ".meta"), null, hash);
            var id = Guid.NewGuid(); var server = Guid.NewGuid();
            await using (var source = db.CreateCommand("INSERT INTO gc.catalog_sources(repository,branch,succeeded_at) VALUES('test/launch','fixture',now())")) await source.ExecuteNonQueryAsync();
            await using (var insert = db.CreateCommand("""
                INSERT INTO gc.catalog_missions(id,repository,scenario_guid,world_guid,scenario_path,name,author,description,tags,max_players,metadata,source_hash)
                VALUES($1,'test/launch',$2,$3,$4,$5,'GC fixture','Dedicated launch test',ARRAY['Test fixture'],2,$6::jsonb,$7)
                """))
            {
                insert.Parameters.AddWithValue(id); insert.Parameters.AddWithValue(mission.ScenarioGuid);
                insert.Parameters.AddWithValue(mission.WorldGuid); insert.Parameters.AddWithValue(mission.ScenarioPath);
                insert.Parameters.AddWithValue(mission.Name);
                insert.Parameters.AddWithValue(JsonSerializer.Serialize(new { data = mission.Metadata }));
                insert.Parameters.AddWithValue(hash); await insert.ExecuteNonQueryAsync();
            }
            var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development", WebRootPath = Path.GetFullPath("dist") });
            builder.Logging.ClearProviders(); builder.Services.AddSingleton(db);
            builder.Services.AddRateLimiter(o => o.AddPolicy("intake", _ => System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter("fixture")));
            builder.Configuration["Ingest:Enabled"] = "true"; builder.Configuration["Ingest:ApiKey"] = key;
            builder.Configuration["Ingest:ServerId"] = server.ToString(); builder.Configuration["Ingest:ArchivePath"] = output;
            await using var app = builder.Build(); app.UseRateLimiter(); Integrations.Map(app);
            app.Urls.Add("http://127.0.0.1:0"); await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            client.DefaultRequestHeaders.Authorization = new("Bearer", key);
            var request = new MissionSelection.Launch(Guid.NewGuid(), id, "isolated-launch-test");
            var response = await client.PostAsync("/api/v1/catalog/selection/launch", new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json"));
            response.EnsureSuccessStatusCode();
            await using (var snapshot = db.CreateCommand("SELECT manifest FROM gc.mission_launch_requests WHERE request_id=$1"))
            {
                snapshot.Parameters.AddWithValue(request.RequestId);
                using var manifest = JsonDocument.Parse((string)(await snapshot.ExecuteScalarAsync())!);
                await File.WriteAllTextAsync(Path.Combine(output, "fixture.json"), JsonSerializer.Serialize(new {
                    apiUrl = app.Urls.Single(), apiKey = key, missionId = id, requestId = request.RequestId,
                    manifest = manifest.RootElement, headerSha256 = hash.ToLowerInvariant()
                }));
            }
            Console.WriteLine("READY isolated launch API; request queued. Create stop.txt in the output directory to finish.");
            while (!File.Exists(Path.Combine(output, "stop.txt")))
            {
                await using var query = db.CreateCommand("SELECT status,outcome FROM gc.mission_launch_requests WHERE request_id=$1");
                query.Parameters.AddWithValue(request.RequestId);
                await using var row = await query.ExecuteReaderAsync();
                if (await row.ReadAsync()) await File.WriteAllTextAsync(Path.Combine(output, "outcome.json"), JsonSerializer.Serialize(new {
                    status = row.GetString(0), outcome = row.IsDBNull(1) ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(row.GetString(1))
                }));
                await Task.Delay(1000);
            }
            await app.StopAsync();
        }
        finally
        {
            await using var drop = admin.CreateCommand($"DROP DATABASE {name} WITH (FORCE)"); await drop.ExecuteNonQueryAsync();
        }
    }
}
