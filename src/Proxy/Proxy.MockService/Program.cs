using Proxy.MockService.Options;
using Proxy.MockService.Services;
using Serilog;
using Shared.Infrastructure.Options;

Log.Logger = new LoggerConfiguration()
    .Enrich.WithProperty("Service", "Proxy.MockService")
    .WriteTo.Console()
    .CreateLogger();

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSerilog();

builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection("RabbitMq"));
builder.Services.Configure<ProxyMockOptions>(builder.Configuration.GetSection("ProxyMock"));

builder.Services.AddSingleton<RabbitMqProxyPublisher>();
builder.Services.AddSingleton<FileTransferService>();
builder.Services.AddHostedService<ProxyMockWorker>();

var host = builder.Build();
host.Run();
