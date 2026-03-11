using NetworkA.Activities.Dispatch.Activities;
using Serilog;
using Serilog.Formatting.Compact;
using Shared.Contracts.Interfaces;
using Shared.Infrastructure.Extensions;
using Shared.Infrastructure.Options;
using Shared.Infrastructure.Repositories;
using Shared.Infrastructure.Startup;
using Temporalio.Extensions.Hosting;

Log.Logger = new LoggerConfiguration()
    .Enrich.WithProperty("Service", "NetworkA.Activities.Dispatch")
    .WriteTo.Console()
    .CreateLogger();

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSerilog();

builder.Services.Configure<MongoDbOptions>(builder.Configuration.GetSection("MongoDB"));
builder.Services.Configure<TemporalOptions>(builder.Configuration.GetSection("Temporal"));
builder.Services.Configure<OutboxOptions>(builder.Configuration.GetSection("Outbox"));
builder.Services.AddMongoDb();

builder.Services.AddScoped<IJobRepository, MongoJobRepository>();

var temporalOpts = builder.Configuration.GetSection("Temporal").Get<TemporalOptions>() ?? new TemporalOptions();
builder.Services.AddTemporalClient(opts =>
{
    opts.TargetHost = temporalOpts.TargetHost;
    opts.Namespace = temporalOpts.Namespace;
});

builder.Services
    .AddHostedTemporalWorker(taskQueue: "retry-dispatch-tasks")
    .AddScopedActivities<RetryChunkActivity>()
    .AddScopedActivities<WriteHardFailActivity>();

var host = builder.Build();

// Startup connectivity validation — fail fast on infrastructure errors (FR-021, SC-004)
using (var scope = host.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var db = scope.ServiceProvider.GetRequiredService<MongoDB.Driver.IMongoDatabase>();
    await StartupValidator.ValidateMongoDbAsync(db, "NetworkA.Activities.Dispatch", logger);

    StartupValidator.LogTemporalWorkerRegistered("NetworkA.Activities.Dispatch", "retry-dispatch-tasks", logger);
}

host.Run();
