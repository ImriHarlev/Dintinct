using FFMpegCore;
using NetworkB.Activities.HeavyAssembly.Activities;
using NetworkB.FileAssembly.Extensions;
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
builder.Services.Configure<AsposeOptions>(builder.Configuration.GetSection("Assemblers:docx:Aspose"));
builder.Services.Configure<ProxyConfigOptions>(builder.Configuration.GetSection("ProxyConfig"));
builder.Services.AddFileAssemblers();
builder.Services.AddFileConverters();

var temporalOpts = builder.Configuration.GetSection("Temporal").Get<TemporalOptions>() ?? new TemporalOptions();
builder.Services.AddTemporalClient(opts =>
{
    opts.TargetHost = temporalOpts.TargetHost;
    opts.Namespace = temporalOpts.Namespace;
});

builder.Services
    .AddHostedTemporalWorker(taskQueue: "heavy-assembly-tasks")
    .AddScopedActivities<AssembleFilesActivities>()
    .AddScopedActivities<RepackAndFinalizeActivities>();

var host = builder.Build();

using (var scope = host.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    StartupValidator.LogTemporalWorkerRegistered("NetworkB.Activities.HeavyAssembly", "heavy-assembly-tasks", logger);
    TempWorkspaceCleaner.Sweep(logger);
}

host.Run();
