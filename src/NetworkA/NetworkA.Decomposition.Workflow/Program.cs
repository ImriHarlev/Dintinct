using NetworkA.Decomposition.Workflow.Activities;
using NetworkA.Decomposition.Workflow.Workflows;
using Shared.Infrastructure.Extensions;
using Shared.Infrastructure.Logging;
using Shared.Infrastructure.Options;
using Shared.Infrastructure.Startup;
using Temporalio.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.AddSharedConfiguration();
builder.AddSerilogFromConfiguration();

builder.Services.Configure<TemporalOptions>(builder.Configuration.GetSection("Temporal"));
builder.Services.Configure<WorkflowActivityConfigOptions>(builder.Configuration.GetSection("WorkflowActivityConfig"));
builder.Services.Configure<ProxyConfigOptions>(builder.Configuration.GetSection("ProxyConfig"));

var temporalOpts = builder.Configuration.GetSection("Temporal").Get<TemporalOptions>() ?? new TemporalOptions();

builder.Services
    .AddHostedTemporalWorker(temporalOpts.TargetHost, temporalOpts.Namespace, "decomposition-workflow")
    .AddWorkflow<DecompositionWorkflow>()
    .AddSingletonActivities<DecompositionConfigLocalActivity>();

var host = builder.Build();

using (var scope = host.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    StartupValidator.LogTemporalWorkerRegistered("NetworkA.Decomposition.Workflow", "decomposition-workflow", logger);
}

host.Run();
