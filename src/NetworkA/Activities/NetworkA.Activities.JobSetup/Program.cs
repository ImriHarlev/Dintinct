using NetworkA.Activities.JobSetup.Activities;
using NetworkA.Activities.JobSetup.Cache;
using NetworkA.Activities.JobSetup.Extensions;
using NetworkA.Activities.JobSetup.Interfaces;
using NetworkA.Activities.JobSetup.Options;
using NetworkA.Activities.JobSetup.Repositories;
using Serilog;
using Serilog.Formatting.Compact;
using Shared.Contracts.Interfaces;
using Shared.Infrastructure.Extensions;
using Shared.Infrastructure.Options;
using Shared.Infrastructure.Repositories;
using Shared.Infrastructure.Startup;
using Temporalio.Extensions.Hosting;

Log.Logger = new LoggerConfiguration()
    .Enrich.WithProperty("Service", "NetworkA.Activities.JobSetup")
    .WriteTo.Console()
    .CreateLogger();

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSerilog();

builder.Services.Configure<MongoDbOptions>(builder.Configuration.GetSection("MongoDB"));
builder.Services.Configure<RedisOptions>(builder.Configuration.GetSection("Redis"));
builder.Services.Configure<TemporalOptions>(builder.Configuration.GetSection("Temporal"));
builder.Services.Configure<RetryPolicyOptions>(builder.Configuration.GetSection("RetryPolicy"));

builder.Services.AddMongoDb();
builder.Services.AddRedis();

builder.Services.AddScoped<IJobRepository, MongoJobRepository>();
builder.Services.AddScoped<IProxyConfigRepository, MongoProxyConfigRepository>();
builder.Services.AddScoped<IProxyConfigCache, RedisProxyConfigCache>();

var temporalOpts = builder.Configuration.GetSection("Temporal").Get<TemporalOptions>() ?? new TemporalOptions();
builder.Services.AddTemporalClient(opts =>
{
    opts.TargetHost = temporalOpts.TargetHost;
    opts.Namespace = temporalOpts.Namespace;
});

builder.Services
    .AddHostedTemporalWorker(taskQueue: "setup-tasks")
    .AddScopedActivities<JobSetupActivities>();

var host = builder.Build();

// Startup connectivity validation — fail fast on infrastructure errors (FR-021, SC-004)
using (var scope = host.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var db = scope.ServiceProvider.GetRequiredService<MongoDB.Driver.IMongoDatabase>();
    await StartupValidator.ValidateMongoDbAsync(db, "NetworkA.Activities.JobSetup", logger);

    var redis = scope.ServiceProvider.GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>();
    await StartupValidator.ValidateRedisAsync(redis, "NetworkA.Activities.JobSetup", logger);

    StartupValidator.LogTemporalWorkerRegistered("NetworkA.Activities.JobSetup", "setup-tasks", logger);
}

host.Run();
