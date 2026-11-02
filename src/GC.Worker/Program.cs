using GC.Infrastructure;
using GC.Worker;
using Quartz;

var builder = Host.CreateApplicationBuilder(args);
var connection = builder.Configuration.GetConnectionString("Gc")
    ?? throw new InvalidOperationException("ConnectionStrings__Gc is required. The worker never falls back to an in-memory database.");
builder.Services.AddWindowsService(options => options.ServiceName = "GC Worker");
builder.Services.AddSingleton(_ => Database.Open(connection));
var telemetryEnabled = builder.Configuration.GetValue<bool>("Telemetry:Enabled");
var missionsEnabled = builder.Configuration.GetValue<bool>("Missions:Enabled");
var deliveryEnabled = builder.Configuration.GetValue<bool>("Delivery:Enabled");
if ((telemetryEnabled || missionsEnabled || deliveryEnabled) && (!Path.IsPathFullyQualified(builder.Configuration["Ingest:ArchivePath"] ?? "")))
    throw new InvalidOperationException("Data processing requires an absolute private Ingest:ArchivePath.");
if(deliveryEnabled&&(!Path.IsPathFullyQualified(builder.Configuration["Delivery:ProfilePath"]??"")||
    !Guid.TryParse(builder.Configuration["Ingest:ServerId"],out var server)||server==Guid.Empty))
    throw new InvalidOperationException("Delivery requires an absolute server profile path and Ingest:ServerId.");
builder.Services.AddQuartz("GC", options =>
{
    options.UseDefaultThreadPool(pool => pool.MaxConcurrency = 2);
    options.UsePersistentStore(store =>
    {
        store.ConfigureStore(settings =>
        {
            settings.StoreJobDataAsStrings = true;
            settings.TablePrefix = "gc_jobs.qrtz_";
        });
        store.UsePostgres(postgres =>
        {
            postgres.ConnectionString = connection;
        });
        store.UseSystemTextJsonSerializer();
        store.UseClustering();
    });
    var key = new JobKey("heartbeat");
    options.AddJob<HeartbeatJob>(job => job.WithIdentity(key));
    options.AddTrigger(trigger => trigger.WithIdentity("heartbeat-every-30-seconds")
        .ForJob(key).WithSimpleSchedule(schedule => schedule.WithInterval(TimeSpan.FromSeconds(30))
            .RepeatForever()));
    if (telemetryEnabled)
    {
        var telemetry = new JobKey("telemetry-projection");
        options.AddJob<TelemetryJob>(job => job.WithIdentity(telemetry));
        options.AddTrigger(trigger => trigger.WithIdentity("telemetry-every-5-seconds").ForJob(telemetry)
            .WithSimpleSchedule(schedule => schedule.WithInterval(TimeSpan.FromSeconds(5)).RepeatForever()));
    }
    if(missionsEnabled)
    {
        var missions=new JobKey("mission-projection");
        options.AddJob<MissionJob>(job=>job.WithIdentity(missions));
        options.AddTrigger(trigger=>trigger.WithIdentity("missions-every-5-seconds").ForJob(missions)
            .WithSimpleSchedule(schedule=>schedule.WithInterval(TimeSpan.FromSeconds(5)).RepeatForever()));
    }
    if(deliveryEnabled)
    {
        var delivery=new JobKey("outbox-delivery");
        options.AddJob<DeliveryJob>(job=>job.WithIdentity(delivery));
        options.AddTrigger(trigger=>trigger.WithIdentity("delivery-every-5-seconds").ForJob(delivery)
            .WithSimpleSchedule(schedule=>schedule.WithInterval(TimeSpan.FromSeconds(5)).RepeatForever()));
    }
});
builder.Services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);
var host = builder.Build();
if (!await Database.IsReady(host.Services.GetRequiredService<Npgsql.NpgsqlDataSource>(), default))
    throw new InvalidOperationException("Apply database migrations before starting the worker.");
await host.RunAsync();

