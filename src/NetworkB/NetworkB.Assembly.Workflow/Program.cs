using NetworkB.Assembly.Workflow.Workflows;
using Serilog;
using Serilog.Formatting.Compact;
using Shared.Infrastructure.Extensions;
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

builder.Services.AddTemporalClient();

builder.Services
    .AddHostedTemporalWorker(taskQueue: "assembly-workflow")
    .AddWorkflow<AssemblyWorkflow>();

var host = builder.Build();

// Log Temporal worker registration (FR-021, SC-002)
using (var scope = host.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    StartupValidator.LogTemporalWorkerRegistered("NetworkB.Assembly.Workflow", "assembly-workflow", logger);
}

host.Run();
