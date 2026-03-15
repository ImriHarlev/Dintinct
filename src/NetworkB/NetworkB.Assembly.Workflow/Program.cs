using NetworkB.Assembly.Workflow.Workflows;
using Serilog;
using Serilog.Formatting.Compact;
using Shared.Infrastructure.Activities;
using Shared.Infrastructure.Extensions;
using Shared.Infrastructure.Options;
using Shared.Infrastructure.Startup;
using Temporalio.Extensions.Hosting;

Log.Logger = new LoggerConfiguration()
    .Enrich.WithProperty("Service", "NetworkB.Assembly.Workflow")
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
builder.Services.AddTemporalClient();

builder.Services
    .AddHostedTemporalWorker(taskQueue: "assembly-workflow")
    .AddWorkflow<AssemblyWorkflow>()
    .AddScopedActivities<WorkflowActivityConfigLocalActivity>();

var host = builder.Build();

using (var scope = host.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var db = scope.ServiceProvider.GetRequiredService<MongoDB.Driver.IMongoDatabase>();
    await StartupValidator.ValidateMongoDbAsync(db, "NetworkB.Assembly.Workflow", logger);

    var redis = scope.ServiceProvider.GetRequiredService<StackExchange.Redis.IConnectionMultiplexer>();
    await StartupValidator.ValidateRedisAsync(redis, "NetworkB.Assembly.Workflow", logger);

    StartupValidator.LogTemporalWorkerRegistered("NetworkB.Assembly.Workflow", "assembly-workflow", logger);
}

host.Run();
