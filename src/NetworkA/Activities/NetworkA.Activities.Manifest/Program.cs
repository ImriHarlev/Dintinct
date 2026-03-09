using NetworkA.Activities.Manifest.Activities;
using Serilog;
using Serilog.Formatting.Compact;
using Shared.Infrastructure.Options;
using Shared.Infrastructure.Startup;
using Temporalio.Extensions.Hosting;

Log.Logger = new LoggerConfiguration()
    .Enrich.WithProperty("Service", "NetworkA.Activities.Manifest")
    .WriteTo.Console(new RenderedCompactJsonFormatter())
    .CreateLogger();

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSerilog();

builder.Services.Configure<TemporalOptions>(builder.Configuration.GetSection("Temporal"));
builder.Services.Configure<OutboxOptions>(builder.Configuration.GetSection("Outbox"));

var temporalOpts = builder.Configuration.GetSection("Temporal").Get<TemporalOptions>() ?? new TemporalOptions();
builder.Services.AddTemporalClient(opts =>
{
    opts.TargetHost = temporalOpts.TargetHost;
    opts.Namespace = temporalOpts.Namespace;
});

builder.Services
    .AddHostedTemporalWorker(taskQueue: "manifest-tasks")
    .AddScopedActivities<ManifestActivities>();

var host = builder.Build();

// Log Temporal worker registration (FR-021, SC-002)
using (var scope = host.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    StartupValidator.LogTemporalWorkerRegistered("NetworkA.Activities.Manifest", "manifest-tasks", logger);
}

host.Run();
