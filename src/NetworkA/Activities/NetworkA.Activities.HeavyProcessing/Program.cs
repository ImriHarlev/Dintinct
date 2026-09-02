using FFMpegCore;
using NetworkA.Activities.HeavyProcessing.Activities;
using NetworkA.FileProcessing.Extensions;
using Shared.Infrastructure.Extensions;
using Shared.Infrastructure.Logging;
using Shared.Infrastructure.Options;
using Shared.Infrastructure.Startup;
using Temporalio.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.AddSharedConfiguration();
builder.AddSerilogFromConfiguration();

var ffmpegFolder = builder.Configuration["FFmpeg:BinaryFolder"];
if (!string.IsNullOrWhiteSpace(ffmpegFolder))
    GlobalFFOptions.Configure(opts => opts.BinaryFolder = ffmpegFolder);

builder.Services.Configure<TemporalOptions>(builder.Configuration.GetSection("Temporal"));
builder.Services.Configure<OutboxOptions>(builder.Configuration.GetSection("Outbox"));
builder.Services.Configure<AsposeOptions>(builder.Configuration.GetSection("Splitters:docx:Aspose"));
builder.Services.Configure<ProxyConfigOptions>(builder.Configuration.GetSection("ProxyConfig"));
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
    TempWorkspaceCleaner.Sweep(logger);
}

host.Run();
