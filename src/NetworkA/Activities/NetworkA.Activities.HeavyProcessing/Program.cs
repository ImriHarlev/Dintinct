using NetworkA.Activities.HeavyProcessing.Activities;
using NetworkA.FileProcessing.Extensions;
using Serilog;
using Shared.Infrastructure.Options;
using Shared.Infrastructure.Startup;
using Temporalio.Extensions.Hosting;

Log.Logger = new LoggerConfiguration()
    .Enrich.WithProperty("Service", "NetworkA.Activities.HeavyProcessing")
    .WriteTo.Console()
    .CreateLogger();

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSerilog();

builder.Services.Configure<TemporalOptions>(builder.Configuration.GetSection("Temporal"));
builder.Services.Configure<OutboxOptions>(builder.Configuration.GetSection("Outbox"));
builder.Services.Configure<AsposeOptions>(builder.Configuration.GetSection("Splitters:docx:Aspose"));
builder.Services.AddFileSplitters();
builder.Services.AddFileConverters();

var temporalOpts = builder.Configuration.GetSection("Temporal").Get<TemporalOptions>() ?? new TemporalOptions();
builder.Services.AddTemporalClient(opts =>
{
    opts.TargetHost = temporalOpts.TargetHost;
    opts.Namespace = temporalOpts.Namespace;
});

builder.Services
    .AddHostedTemporalWorker(taskQueue: "heavy-processing-tasks")
    .AddScopedActivities<PrepareSourceActivities>()
    .AddScopedActivities<DecomposeAndSplitActivities>();

var host = builder.Build();

using (var scope = host.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    StartupValidator.LogTemporalWorkerRegistered("NetworkA.Activities.HeavyProcessing", "heavy-processing-tasks", logger);
}

host.Run();
