using GC.Infrastructure;
using Npgsql;
using Quartz;

namespace GC.Worker;

[DisallowConcurrentExecution]
public sealed class MissionJob(NpgsqlDataSource database,IConfiguration configuration):IJob
{
    public async ValueTask Execute(IJobExecutionContext context,CancellationToken token)
    {
        if(!configuration.GetValue<bool>("Missions:Enabled"))return;
        for(var count=0;count<8&&await MissionProjection.ProjectNext(database,configuration["Ingest:ArchivePath"]!,token);count++){}
    }
}
