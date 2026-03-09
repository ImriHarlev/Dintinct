using NetworkB.Activities.ManifestState.Activities;
using NetworkB.Activities.ManifestState.Repositories;
using Serilog;
using Serilog.Formatting.Compact;
using Shared.Contracts.Interfaces;
using Shared.Infrastructure.Extensions;
using Shared.Infrastructure.Options;
using Shared.Infrastructure.Startup;
using Temporalio.Extensions.Hosting;

Log.Logger = new LoggerConfiguration()
    .Enrich.WithProperty("Service", "NetworkB.Activities.ManifestState")
    .WriteTo.Console(new RenderedCompactJsonFormatter())
    .CreateLogger();

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSerilog();

builder.Services.Configure<MongoDbOptions>(builder.Configuration.GetSection("MongoDB"));
builder.Services.Configure<TemporalOptions>(builder.Configuration.GetSection("Temporal"));

builder.Services.AddMongoDb();

builder.Services.AddScoped<IAssemblyBlueprintRepository, MongoManifestRepository>();

var temporalOpts = builder.Configuration.GetSection("Temporal").Get<TemporalOptions>() ?? new TemporalOptions();
builder.Services.AddTemporalClient(opts =>
{
    opts.TargetHost = temporalOpts.TargetHost;
    opts.Namespace = temporalOpts.Namespace;
});

builder.Services
    .AddHostedTemporalWorker(taskQueue: "manifest-assembly-tasks")
    .AddScopedActivities<ManifestStateActivities>();

var host = builder.Build();

// Startup connectivity validation — fail fast on infrastructure errors (FR-021, SC-004)
using (var scope = host.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var db = scope.ServiceProvider.GetRequiredService<MongoDB.Driver.IMongoDatabase>();
    await StartupValidator.ValidateMongoDbAsync(db, "NetworkB.Activities.ManifestState", logger);

    StartupValidator.LogTemporalWorkerRegistered("NetworkB.Activities.ManifestState", "manifest-assembly-tasks", logger);
}

host.Run();
