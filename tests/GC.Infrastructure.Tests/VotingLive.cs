using System.Security.Cryptography;
using System.Text.Json;
using GC.Infrastructure;
using GC.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

// Explicit, isolated MCP fixture. Never imports fictional missions into the user's database.
internal static class VotingLive
{
    public static async Task Run(string profile, string addon)
    {
        var connection = Environment.GetEnvironmentVariable("GC_TEST_ADMIN_CONNECTION") ?? throw new Exception("Test connection required");
        var name = "gc_test_vote_" + Guid.NewGuid().ToString("N");
        var archive = Path.Combine(Path.GetTempPath(), name);
        Directory.CreateDirectory(archive);
        var configuration = Path.Combine(profile, "Lobby2Voting.json");
        var original = File.Exists(configuration) ? await File.ReadAllBytesAsync(configuration) : null;
        await using var admin = Database.Open(connection);
        await using (var create = admin.CreateCommand($"CREATE DATABASE {name}")) await create.ExecuteNonQueryAsync();
        try
        {
            await using var db = Database.Open(new NpgsqlConnectionStringBuilder(connection) { Database = name, Pooling = false }.ConnectionString);
            await Database.Migrate(db);
            await using (var source = db.CreateCommand("INSERT INTO gc.catalog_sources(repository,branch,succeeded_at) VALUES('test/voting','fixture',now())")) await source.ExecuteNonQueryAsync();
            string[] titles = ["Coastal Raid", "Arland Patrol", "Roadblock", "Night Watch", "Airfield Assault", "Supply Run"];
            for (var i = 0; i < titles.Length; i++)
            {
                var header = Path.Combine(addon, "tests/McpTestAddon/Missions/Voting", $"Vote{i}.conf");
                var mission = CatalogSync.Parse($"Missions/Voting/Vote{i}.conf", await File.ReadAllTextAsync(header), await File.ReadAllTextAsync(header + ".meta"), null, "fixture");
                await using var insert = db.CreateCommand("""
                    INSERT INTO gc.catalog_missions(id,repository,scenario_guid,world_guid,scenario_path,name,author,description,tags,max_players,metadata,source_hash)
                    VALUES($1,'test/voting',$2,$3,$4,$5,'MCP fixture',$6,ARRAY['Test fixture','Infantry'],$8,$7::jsonb,'test')
                    """);
                insert.Parameters.AddWithValue(Guid.NewGuid()); insert.Parameters.AddWithValue(mission.ScenarioGuid);
                insert.Parameters.AddWithValue(mission.WorldGuid); insert.Parameters.AddWithValue(mission.ScenarioPath);
                insert.Parameters.AddWithValue(titles[i]);
                insert.Parameters.AddWithValue(i == 0 ? string.Concat(Enumerable.Repeat("Long description fixture: secure the coastal objective, cover the approach and coordinate resupply with your team. ", 6)) : "Test fixture. Secure the objective and support your team across Arland.");
                var metadata = System.Text.Json.Nodes.JsonNode.Parse(mission.Metadata.GetRawText())!;
                metadata["minPlayers"] = i == 3 ? 50 : 16;
                insert.Parameters.AddWithValue(JsonSerializer.Serialize(new { data = metadata }));
                insert.Parameters.AddWithValue(i == 3 ? 100 : 32);
                await insert.ExecuteNonQueryAsync();
            }
            var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development", WebRootPath = Path.GetFullPath("dist") });
            builder.Logging.ClearProviders(); builder.Services.AddSingleton(db);
            builder.Services.AddRateLimiter(o => o.AddPolicy("intake", _ => System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter("fixture")));
            builder.Configuration["Ingest:Enabled"] = "true"; builder.Configuration["Ingest:ApiKey"] = key;
            builder.Configuration["Ingest:ServerId"] = Guid.NewGuid().ToString(); builder.Configuration["Ingest:ArchivePath"] = archive;
            await using var app = builder.Build(); app.UseRateLimiter(); Integrations.Map(app);
            app.Urls.Add("http://127.0.0.1:5099"); await app.StartAsync();
            await File.WriteAllTextAsync(configuration, JsonSerializer.Serialize(new { url = "http://127.0.0.1:5099/", apiKey = key }));
            Console.WriteLine("READY isolated voting catalog on loopback:5099; private profile configuration installed. Press Enter to stop and restore.");
            Console.ReadLine();
            await app.StopAsync();
        }
        finally
        {
            if (original is null) File.Delete(configuration); else await File.WriteAllBytesAsync(configuration, original);
            await using var drop = admin.CreateCommand($"DROP DATABASE {name} WITH (FORCE)"); await drop.ExecuteNonQueryAsync();
        }
    }
}
