using NetworkB.Activities.HeavyAssembly.Activities;
using NetworkB.Activities.HeavyAssembly.Assemblers;
using Serilog;
using Shared.Infrastructure.Extensions;
using Shared.Infrastructure.Options;
using Shared.Infrastructure.Startup;
using Temporalio.Extensions.Hosting;

Log.Logger = new LoggerConfiguration()
    .Enrich.WithProperty("Service", "NetworkB.Activities.HeavyAssembly")
    .WriteTo.Console()
    .CreateLogger();

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSerilog();

builder.Services.Configure<TemporalOptions>(builder.Configuration.GetSection("Temporal"));
builder.Services.Configure<AsposeOptions>(builder.Configuration.GetSection("Assemblers:docx:Aspose"));
builder.Services.AddScoped<FileAssemblerFactory>();
builder.Services.AddScoped<DefaultFileAssembler>();
builder.Services.AddScoped<IFileAssembler, DocsAssembler>();

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

// Log Temporal worker registration (FR-021, SC-002)
using (var scope = host.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    StartupValidator.LogTemporalWorkerRegistered("NetworkB.Activities.HeavyAssembly", "heavy-assembly-tasks", logger);
}

host.Run();
