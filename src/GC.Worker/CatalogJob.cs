using GC.Infrastructure;
using Npgsql;
using Quartz;
namespace GC.Worker;

[DisallowConcurrentExecution]
public sealed class CatalogJob(NpgsqlDataSource database,IConfiguration configuration,ILogger<CatalogJob> logger):IJob
{
    internal static async Task RefreshLocal(NpgsqlDataSource database, IConfiguration configuration, CancellationToken token)
    {
        if (configuration["Catalog:LocalMission:Manifest"] is not { Length: > 0 } manifest) return;
        var header = configuration["Catalog:LocalMission:Header"] ?? throw new InvalidDataException("Missing local mission header");
        var snapshot = configuration["Catalog:LocalMission:Snapshot"] ?? throw new InvalidDataException("Missing local mission snapshot");
        var json = LocalMissionImport.WithHeader(await File.ReadAllTextAsync(manifest, token),
            await File.ReadAllTextAsync(header, token), await File.ReadAllTextAsync(snapshot, token));
        await LocalMissionImport.Import(database, json, false, token);
    }

    public async ValueTask Execute(IJobExecutionContext context,CancellationToken token)
    {
        try { await RefreshLocal(database, configuration, token); }
        catch (Exception error) when (!token.IsCancellationRequested) { logger.LogWarning("Local mission refresh failed: {Type}", error.GetType().Name); }
        if(!configuration.GetValue<bool>("Catalog:Enabled"))return;
        using var http=new HttpClient{Timeout=TimeSpan.FromSeconds(60)};
        var sync=new CatalogSync(http);
        foreach(var source in configuration.GetSection("Catalog:Sources").Get<CatalogSource[]>()??[])
        {
            try{await sync.Sync(database,source,token);}
            catch(Exception error)when(!token.IsCancellationRequested){logger.LogWarning("Mission catalogue update failed for {Repository}: {Type}",source.Repository,error.GetType().Name);}
        }
    }
}
