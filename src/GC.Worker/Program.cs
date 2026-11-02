using GC.Infrastructure;
using GC.Worker;
using Quartz;

var builder = Host.CreateApplicationBuilder(args);
var connection = builder.Configuration.GetConnectionString("Gc")
    ?? throw new InvalidOperationException("ConnectionStrings__Gc is required. The worker never falls back to an in-memory database.");
builder.Services.AddWindowsService(options => options.ServiceName = "GC Worker");
builder.Services.AddSingleton(_ => Database.Open(connection));
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
});
builder.Services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);
var host = builder.Build();
if (!await Database.IsReady(host.Services.GetRequiredService<Npgsql.NpgsqlDataSource>(), default))
    throw new InvalidOperationException("Apply database migrations before starting the worker.");
await host.RunAsync();

