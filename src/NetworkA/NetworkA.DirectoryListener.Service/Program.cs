using NetworkA.DirectoryListener.Service.Options;
using NetworkA.DirectoryListener.Service.Services;
using Serilog;
using Shared.Infrastructure.Extensions;
using Shared.Infrastructure.Options;
using Shared.Infrastructure.Startup;
using ZiggyCreatures.Caching.Fusion;

Log.Logger = new LoggerConfiguration()
    .Enrich.WithProperty("Service", "NetworkA.DirectoryListener.Service")
    .WriteTo.Console()
    .CreateLogger();

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSerilog();

builder.Services.Configure<TemporalOptions>(builder.Configuration.GetSection("Temporal"));
builder.Services.Configure<DirectoryListenerOptions>(builder.Configuration.GetSection(DirectoryListenerOptions.SectionName));

builder.Services.AddTemporalClient();

builder.Services.AddFusionCache();

builder.Services.AddSingleton<IInputSubmissionService, InputSubmissionService>();
builder.Services.AddHostedService<DirectoryListenerBackgroundService>();

var host = builder.Build();

using (var scope = host.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    StartupValidator.LogTemporalWorkerRegistered("NetworkA.DirectoryListener.Service", "n/a (client only)", logger);
}

await host.RunAsync();
