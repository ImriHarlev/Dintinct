using NetworkA.Callback.Receiver.Interfaces;
using NetworkA.Callback.Receiver.Services;
using Serilog;
using Serilog.Formatting.Compact;
using Shared.Contracts.Interfaces;
using Shared.Infrastructure.Extensions;
using Shared.Infrastructure.Options;
using Shared.Infrastructure.Repositories;
using Shared.Infrastructure.Startup;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, config) => config
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.WithProperty("Service", "NetworkA.Callback.Receiver")
    .WriteTo.Console());

builder.Services.Configure<MongoDbOptions>(builder.Configuration.GetSection("MongoDB"));
builder.Services.Configure<TemporalOptions>(builder.Configuration.GetSection("Temporal"));

builder.Services.AddMongoDb();
builder.Services.AddTemporalClient();

builder.Services.AddScoped<IJobRepository, MongoJobRepository>();
builder.Services.AddScoped<ICallbackService, CallbackService>();

builder.Services.AddControllers();

var app = builder.Build();

app.MapControllers();

// Startup connectivity validation — fail fast on infrastructure errors (FR-021, SC-004)
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var db = scope.ServiceProvider.GetRequiredService<MongoDB.Driver.IMongoDatabase>();
    await StartupValidator.ValidateMongoDbAsync(db, "NetworkA.Callback.Receiver", logger);
}

app.Run();
