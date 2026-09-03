using NetworkB.Activities.Reporting.Activities;
using NetworkB.Activities.Reporting.Interfaces;
using NetworkB.Activities.Reporting.Services;
using Shared.Infrastructure.Extensions;
using Shared.Infrastructure.Logging;
using Shared.Infrastructure.Options;
using Shared.Infrastructure.Startup;
using Temporalio.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.AddSerilogFromConfiguration();

builder.Services.Configure<TemporalOptions>(builder.Configuration.GetSection("Temporal"));
builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection("RabbitMq"));
builder.Services.AddScoped<ICsvReportWriter, CsvReportWriter>();
builder.Services.AddKeyedScoped<IAnswerDispatcher, RabbitMqAnswerDispatcher>("RabbitMQ");
builder.Services.AddKeyedScoped<IAnswerDispatcher, FileSystemAnswerDispatcher>("FileSystem");
builder.Services.AddScoped<IAnswerDispatcherFactory, AnswerDispatcherFactory>();

var temporalOpts = builder.Configuration.GetSection("Temporal").Get<TemporalOptions>() ?? new TemporalOptions();
builder.Services.AddTemporalClient(opts =>
{
    opts.TargetHost = temporalOpts.TargetHost;
    opts.Namespace = temporalOpts.Namespace;
});

builder.Services
    .AddHostedTemporalWorker(taskQueue: "callback-dispatch-tasks")
    .AddScopedActivities<WriteCsvReportActivities>()
    .AddScopedActivities<DispatchAnswerActivities>();

var host = builder.Build();

using (var scope = host.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    StartupValidator.LogTemporalWorkerRegistered("NetworkB.Activities.Reporting", "callback-dispatch-tasks", logger);
}

host.Run();
