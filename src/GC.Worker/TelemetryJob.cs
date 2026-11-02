using GC.Infrastructure;
using Npgsql;
using Quartz;

namespace GC.Worker;

[DisallowConcurrentExecution]
public sealed class TelemetryJob(NpgsqlDataSource database, IConfiguration configuration) : IJob
{
    public async ValueTask Execute(IJobExecutionContext context, CancellationToken token)
    {
        // A persisted trigger can outlive the configuration that created it.
        if (!configuration.GetValue<bool>("Telemetry:Enabled")) return;
        var archive = configuration["Ingest:ArchivePath"]!;
        for (var count = 0; count < 8 && await TelemetryProjection.ProjectNext(database, archive, token); count++) { }
    }
}
