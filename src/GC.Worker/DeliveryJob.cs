using GC.Infrastructure;
using Npgsql;
using Quartz;

namespace GC.Worker;

[DisallowConcurrentExecution]
public sealed class DeliveryJob(NpgsqlDataSource database,IConfiguration configuration):IJob
{
    public async ValueTask Execute(IJobExecutionContext context,CancellationToken token)
    {
        if(!configuration.GetValue<bool>("Delivery:Enabled"))return;
        await OutboxDelivery.Deliver(database,configuration["Delivery:ProfilePath"]!,configuration["Ingest:ArchivePath"]!,
            Guid.Parse(configuration["Ingest:ServerId"]!),token);
    }
}
