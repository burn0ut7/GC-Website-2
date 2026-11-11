using GC.Infrastructure;
using Npgsql;
using System.Diagnostics;

if (args.Length == 3 && args[0] == "--voting-live") { await VotingLive.Run(args[1],args[2]); return; }

if (args.Contains("--mission-metadata")) { MissionMetadataChecks.Run(); return; }
if (Array.IndexOf(args,"--mission-source") is var sourceAt && sourceAt>=0)
{
    var count=0;var failures=0;
    foreach(var file in Directory.EnumerateFiles(args[sourceAt+1],"*.conf",SearchOption.AllDirectories))
    {
        if(!File.Exists(file+".meta"))continue;
        try{CatalogSync.Parse(file,File.ReadAllText(file),File.ReadAllText(file+".meta"),null,"source-check");count++;}
        catch(Exception error){Console.WriteLine(Path.GetFileName(file)+": "+error.Message);failures++;}
    }
    Console.WriteLine($"Source headers accepted: {count}; rejected: {failures}");
    if(failures>0)Environment.ExitCode=1;
    return;
}

if (args.Contains("--account-roles")) { await AccountRoleChecks.Run(); return; }

if (args.Contains("--voice")) { VoiceChecks.Run(); return; }
if (args.Contains("--catalog")) { await CatalogChecks.Run(); return; }

if (args.Contains("--explorer")) { await ExplorerChecks.Run(); return; }
if (args.Contains("--discord-roles")) { await DiscordRoleChecks.Run(); return; }
if (args.Contains("--map-packages")) { await MapPackageChecks.Run(); return; }

var adminConnection = Environment.GetEnvironmentVariable("GC_TEST_ADMIN_CONNECTION")
    ?? throw new InvalidOperationException("Set GC_TEST_ADMIN_CONNECTION to a local PostgreSQL test administrator. Tests create and drop only a random gc_test_* database.");
var name = "gc_test_" + Guid.NewGuid().ToString("N");
await using var admin = Database.Open(adminConnection);
await using (var create = admin.CreateCommand($"CREATE DATABASE {name}")) await create.ExecuteNonQueryAsync();
try
{
    var settings = new NpgsqlConnectionStringBuilder(adminConnection) { Database = name, Pooling = false };
    await using var database = Database.Open(settings.ConnectionString);
    await Task.WhenAll(Database.Migrate(database), Database.Migrate(database));
    await Database.Migrate(database);
    Check(await Database.IsReady(database, default), "Concurrent and repeated migrations are idempotent");
    await using (var change = database.CreateCommand("UPDATE gc.schema_migrations SET checksum='changed'"))
        await change.ExecuteNonQueryAsync();
    Check(!await Database.IsReady(database, default), "Readiness detects changed migrations");
    try { await Database.Migrate(database); throw new Exception("Changed migration accepted"); }
    catch (InvalidOperationException) { Console.WriteLine("PASS: Changed migrations rejected"); }
    await using (var reset = database.CreateCommand("DROP SCHEMA gc CASCADE; DROP SCHEMA IF EXISTS gc_jobs CASCADE")) await reset.ExecuteNonQueryAsync();
    await Database.Migrate(database);
    Check(await Database.IsReady(database, default), "Fresh schema migration succeeds");
    var signIns = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Accounts.SignInDiscord(database, "123456789012345678", "Test member", default)));
    Check(signIns.Distinct().Count() == 1, "Concurrent Discord sign-ins resolve to one GC account");
    await IdentityChecks.Run(database,args.Contains("--steam"));
    if(args.Contains("--steam"))return;
    var web = Environment.GetEnvironmentVariable("GC_TEST_WEB_DLL");
    if (!string.IsNullOrWhiteSpace(web)) await ApiChecks.Run(web, settings.ConnectionString);
    var worker = Environment.GetEnvironmentVariable("GC_TEST_WORKER_DLL");
    if (!string.IsNullOrWhiteSpace(worker))
    {
        DateTime lastSeen = DateTime.MinValue;
        for (var run = 0; run < 2; run++)
        {
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(worker);
            start.Environment["ConnectionStrings__Gc"] = settings.ConnectionString;
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(75);
                var advanced = false;
                while (DateTime.UtcNow < deadline)
                {
                    if (process.HasExited) throw new Exception("Worker exited: " + await stderr + await stdout);
                    await using var heartbeat = database.CreateCommand("SELECT last_seen_at FROM gc.worker_heartbeat WHERE name='scheduler'");
                    var result = await heartbeat.ExecuteScalarAsync();
                    if (result is DateTime seen && seen > lastSeen) { lastSeen = seen; advanced = true; break; }
                    await Task.Delay(250);
                }
                Check(advanced, "Persistent worker executes after start/restart " + (run + 1));
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                await Task.WhenAll(stdout, stderr);
            }
        }
        await using var jobs = database.CreateCommand("SELECT count(*) FROM gc_jobs.qrtz_job_details WHERE job_name='heartbeat'");
        Check((long)(await jobs.ExecuteScalarAsync())! == 1, "Worker restart does not duplicate scheduled jobs");
    }
    Console.WriteLine("PASS: Database foundation checks complete.");
}
finally
{
    await using var drop = admin.CreateCommand($"DROP DATABASE {name} WITH (FORCE)");
    await drop.ExecuteNonQueryAsync();
}

static void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    Console.WriteLine("PASS: " + description);
}
