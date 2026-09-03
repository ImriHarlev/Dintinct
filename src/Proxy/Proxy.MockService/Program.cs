using Proxy.MockService.Options;
using Proxy.MockService.Services;
using Shared.Infrastructure.Logging;
using Shared.Infrastructure.Options;

var builder = Host.CreateApplicationBuilder(args);
builder.AddSerilogFromConfiguration();

builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection("RabbitMq"));
builder.Services.Configure<ProxyMockOptions>(builder.Configuration.GetSection("ProxyMock"));

builder.Services.AddSingleton<RabbitMqProxyPublisher>();
builder.Services.AddSingleton<FileTransferService>();
builder.Services.AddHostedService<ProxyMockWorker>();

var host = builder.Build();
host.Run();
