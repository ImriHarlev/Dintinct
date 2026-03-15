using NetworkA.Decomposition.Workflow.Workflows;
using Serilog;
using Serilog.Formatting.Compact;
using Shared.Infrastructure.Activities;
using Shared.Infrastructure.Extensions;
using Shared.Infrastructure.Options;
using Shared.Infrastructure.Startup;
using Temporalio.Extensions.Hosting;

Log.Logger = new LoggerConfiguration()
    .Enrich.WithProperty("Service", "NetworkA.Decomposition.Workflow")
    .WriteTo.Console()
    .CreateLogger();

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSerilog();

builder.Services.Configure<TemporalOptions>(builder.Configuration.GetSection("Temporal"));
builder.Services.Configure<MongoDbOptions>(builder.Configuration.GetSection("MongoDB"));
builder.Services.Configure<RedisOptions>(builder.Configuration.GetSection("Redis"));

builder.Services.AddMongoDb();
builder.Services.AddRedis();
builder.Services.AddWorkflowActivityConfig();

var temporalOpts = builder.Configuration.GetSection("Temporal").Get<TemporalOptions>() ?? new TemporalOptions();

builder.Services
    .AddHostedTemporalWorker(temporalOpts.TargetHost, temporalOpts.Namespace, "decomposition-workflow")
    .AddWorkflow<DecompositionWorkflow>()
    .AddScopedActivities<WorkflowActivityConfigLocalActivity>();

var host = builder.Build();

using (var scope = host.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var db = scope.ServiceProvider.GetRequiredService<MongoDB.Driver.IMongoDatabase>();
    await StartupValidator.ValidateMongoDbAsync(db, "NetworkA.Decomposition.Workflow", logger);

    var redis = scope.ServiceProvider.GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>();
    await StartupValidator.ValidateRedisAsync(redis, "NetworkA.Decomposition.Workflow", logger);

    StartupValidator.LogTemporalWorkerRegistered("NetworkA.Decomposition.Workflow", "decomposition-workflow", logger);
}

host.Run();
