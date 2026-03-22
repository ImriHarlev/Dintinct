using NetworkA.DirectoryListener.Service.Options;
using NetworkA.DirectoryListener.Service.Services;
using Serilog;
using Shared.Contracts.Interfaces;
using Shared.Infrastructure.Extensions;
using Shared.Infrastructure.Options;
using Shared.Infrastructure.Repositories;
using Shared.Infrastructure.Startup;

Log.Logger = new LoggerConfiguration()
    .Enrich.WithProperty("Service", "NetworkA.DirectoryListener.Service")
    .WriteTo.Console()
    .CreateLogger();

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSerilog();

builder.Services.Configure<MongoDbOptions>(builder.Configuration.GetSection("MongoDB"));
builder.Services.Configure<TemporalOptions>(builder.Configuration.GetSection("Temporal"));
builder.Services.Configure<DirectoryListenerOptions>(builder.Configuration.GetSection(DirectoryListenerOptions.SectionName));

builder.Services.AddMongoDb();
builder.Services.AddTemporalClient();

builder.Services.AddScoped<IJobRepository, MongoJobRepository>();
builder.Services.AddSingleton<IInputSubmissionService, InputSubmissionService>();
builder.Services.AddHostedService<DirectoryListenerBackgroundService>();

var host = builder.Build();

using (var scope = host.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var db = scope.ServiceProvider.GetRequiredService<MongoDB.Driver.IMongoDatabase>();
    await StartupValidator.ValidateMongoDbAsync(db, "NetworkA.DirectoryListener.Service", logger);
}

await host.RunAsync();
