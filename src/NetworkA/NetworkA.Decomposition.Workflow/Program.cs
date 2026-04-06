using NetworkA.Decomposition.Workflow.Activities;
using NetworkA.Decomposition.Workflow.Workflows;
using Serilog;
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
builder.Services.Configure<WorkflowActivityConfigOptions>(builder.Configuration.GetSection("WorkflowActivityConfig"));
builder.Services.Configure<ProxyConfigOptions>(builder.Configuration.GetSection("ProxyConfig"));
builder.Services.Configure<RetryPolicyOptions>(builder.Configuration.GetSection("RetryPolicy"));

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
