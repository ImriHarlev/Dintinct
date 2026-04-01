using NetworkA.Callback.Receiver.Interfaces;
using NetworkA.Callback.Receiver.Services;
using Serilog;
using Shared.Infrastructure.Extensions;
using Shared.Infrastructure.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, config) => config
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.WithProperty("Service", "NetworkA.Callback.Receiver")
    .WriteTo.Console());

builder.Services.Configure<TemporalOptions>(builder.Configuration.GetSection("Temporal"));
builder.Services.AddTemporalClient();

builder.Services.AddScoped<ICallbackService, CallbackService>();

builder.Services.AddControllers();

var app = builder.Build();

app.MapControllers();

app.Run();
