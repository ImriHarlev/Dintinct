using NetworkA.Decomposition.Workflow.Workflows;
using Serilog;
using Serilog.Formatting.Compact;
using Shared.Infrastructure.Options;
using Shared.Infrastructure.Startup;
using Temporalio.Extensions.Hosting;

Log.Logger = new LoggerConfiguration()
    .Enrich.WithProperty("Service", "NetworkA.Decomposition.Workflow")
    .WriteTo.Console()
    .CreateLogger();

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSerilog();

var temporalOpts = builder.Configuration.GetSection("Temporal").Get<TemporalOptions>() ?? new TemporalOptions();

builder.Services
    .AddHostedTemporalWorker(temporalOpts.TargetHost, temporalOpts.Namespace, "decomposition-workflow")
    .AddWorkflow<DecompositionWorkflow>();

var host = builder.Build();

// Log Temporal worker registration (FR-021, SC-002)
using (var scope = host.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    StartupValidator.LogTemporalWorkerRegistered("NetworkA.Decomposition.Workflow", "decomposition-workflow", logger);
}

host.Run();
