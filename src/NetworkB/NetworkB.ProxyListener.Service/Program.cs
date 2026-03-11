using NetworkB.ProxyListener.Service.Consumers;
using NetworkB.ProxyListener.Service.Options;
using Serilog;
using Serilog.Formatting.Compact;
using Shared.Infrastructure.Extensions;
using Shared.Infrastructure.Options;

Log.Logger = new LoggerConfiguration()
    .Enrich.WithProperty("Service", "NetworkB.ProxyListener.Service")
    .WriteTo.Console()
    .CreateLogger();

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSerilog();

builder.Services.Configure<TemporalOptions>(builder.Configuration.GetSection("Temporal"));
builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection("RabbitMq"));
builder.Services.Configure<ProxyListenerOptions>(builder.Configuration.GetSection("ProxyListener"));
builder.Services.Configure<NetworkACallbackOptions>(builder.Configuration.GetSection("NetworkA"));
builder.Services.Configure<AssemblyOptions>(builder.Configuration.GetSection("Assembly"));

builder.Services.AddTemporalClient();
builder.Services.AddHttpClient("NetworkA");

builder.Services.AddHostedService<ProxyEventConsumer>();

var host = builder.Build();
// Connectivity confirmed by ProxyEventConsumer.StartAsync (RabbitMQ + Temporal)
host.Run();
