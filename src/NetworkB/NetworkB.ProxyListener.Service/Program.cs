using NetworkB.ProxyListener.Service.Consumers;
using NetworkB.ProxyListener.Service.Options;
using Shared.Infrastructure.Extensions;
using Shared.Infrastructure.Logging;
using Shared.Infrastructure.Options;

var builder = Host.CreateApplicationBuilder(args);
builder.AddSerilogFromConfiguration();

builder.Services.Configure<TemporalOptions>(builder.Configuration.GetSection("Temporal"));
builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection("RabbitMq"));
builder.Services.Configure<ProxyListenerOptions>(builder.Configuration.GetSection("ProxyListener"));
builder.Services.Configure<AssemblyOptions>(builder.Configuration.GetSection("Assembly"));

builder.Services.AddTemporalClient();

builder.Services.AddHostedService<ProxyEventConsumer>();

var host = builder.Build();
host.Run();
