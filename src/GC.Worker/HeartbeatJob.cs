using Npgsql;
using Quartz;

namespace GC.Worker;

[DisallowConcurrentExecution]
public sealed class HeartbeatJob(NpgsqlDataSource database) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        await using var command = database.CreateCommand("""
            INSERT INTO gc.worker_heartbeat(name, last_seen_at) VALUES ('scheduler', now())
            ON CONFLICT (name) DO UPDATE SET last_seen_at = excluded.last_seen_at
            """);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
