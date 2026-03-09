using NetworkB.Activities.Reporting.Activities;
using NetworkB.Activities.Reporting.Services;
using Serilog;
using Serilog.Formatting.Compact;
using Shared.Contracts.Interfaces;
using Shared.Infrastructure.Extensions;
using Shared.Infrastructure.Options;
using Shared.Infrastructure.Startup;
using Temporalio.Extensions.Hosting;

Log.Logger = new LoggerConfiguration()
    .Enrich.WithProperty("Service", "NetworkB.Activities.Reporting")
    .WriteTo.Console(new RenderedCompactJsonFormatter())
    .CreateLogger();

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSerilog();

builder.Services.Configure<TemporalOptions>(builder.Configuration.GetSection("Temporal"));
builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection("RabbitMq"));
builder.Services.Configure<NetworkACallbackOptions>(builder.Configuration.GetSection("NetworkA"));
builder.Services.Configure<InboxOptions>(builder.Configuration.GetSection("Inbox"));

builder.Services.AddHttpClient("NetworkA");

builder.Services.AddScoped<ICsvReportWriter, CsvReportWriter>();
builder.Services.AddKeyedScoped<IAnswerDispatcher, RabbitMqAnswerDispatcher>("RabbitMQ");
builder.Services.AddKeyedScoped<IAnswerDispatcher, FileSystemAnswerDispatcher>("FileSystem");
builder.Services.AddScoped<IAnswerDispatcherFactory, AnswerDispatcherFactory>();
builder.Services.AddScoped<INetworkAClient, NetworkAHttpClient>();

var temporalOpts = builder.Configuration.GetSection("Temporal").Get<TemporalOptions>() ?? new TemporalOptions();
builder.Services.AddTemporalClient(opts =>
{
    opts.TargetHost = temporalOpts.TargetHost;
    opts.Namespace = temporalOpts.Namespace;
});

builder.Services
    .AddHostedTemporalWorker(taskQueue: "callback-dispatch-tasks")
    .AddScopedActivities<ReportingActivities>();

var host = builder.Build();

// Log Temporal worker registration (FR-021, SC-002)
using (var scope = host.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    StartupValidator.LogTemporalWorkerRegistered("NetworkB.Activities.Reporting", "callback-dispatch-tasks", logger);
    // RabbitMQ connectivity confirmed when DispatchAsync is called during reporting
}

host.Run();
