using NetworkB.Assembly.Workflow.Activities;
using NetworkB.Assembly.Workflow.Workflows;
using Serilog;
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
builder.Services.Configure<WorkflowActivityConfigOptions>(builder.Configuration.GetSection("WorkflowActivityConfig"));

var temporalOpts = builder.Configuration.GetSection("Temporal").Get<TemporalOptions>() ?? new TemporalOptions();

builder.Services
    .AddHostedTemporalWorker(temporalOpts.TargetHost, temporalOpts.Namespace, "assembly-workflow")
    .AddWorkflow<AssemblyWorkflow>()
    .AddSingletonActivities<AssemblyConfigLocalActivity>();

var host = builder.Build();

using (var scope = host.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    StartupValidator.LogTemporalWorkerRegistered("NetworkB.Assembly.Workflow", "assembly-workflow", logger);
}

host.Run();
