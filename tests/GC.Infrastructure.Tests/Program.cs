using GC.Infrastructure;
using Npgsql;

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
    await using (var reset = database.CreateCommand("DROP SCHEMA gc CASCADE")) await reset.ExecuteNonQueryAsync();
    await Database.Migrate(database);
    Check(await Database.IsReady(database, default), "Fresh schema migration succeeds");
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
