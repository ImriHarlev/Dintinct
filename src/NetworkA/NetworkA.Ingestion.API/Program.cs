using FluentValidation;
using NetworkA.Ingestion.API.Consumers;
using NetworkA.Ingestion.API.Services;
using NetworkA.Ingestion.API.Validators;
using Serilog;
using Serilog.Formatting.Compact;
using Shared.Contracts.Interfaces;
using Shared.Contracts.Payloads;
using Shared.Infrastructure.Extensions;
using Shared.Infrastructure.Options;
using Shared.Infrastructure.Repositories;
using Shared.Infrastructure.Startup;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, config) => config
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.WithProperty("Service", "NetworkA.Ingestion.API")
    .WriteTo.Console(new RenderedCompactJsonFormatter()));

builder.Services.Configure<MongoDbOptions>(builder.Configuration.GetSection("MongoDB"));
builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection("RabbitMq"));
builder.Services.Configure<TemporalOptions>(builder.Configuration.GetSection("Temporal"));
builder.Services.Configure<RetryPolicyOptions>(builder.Configuration.GetSection("RetryPolicy"));

builder.Services.AddMongoDb();
builder.Services.AddTemporalClient();

builder.Services.AddScoped<IJobRepository, MongoJobRepository>();
builder.Services.AddScoped<IIngestionService, IngestionService>();
builder.Services.AddScoped<IValidator<IngestionRequestPayload>, IngestionRequestValidator>();

builder.Services.AddHostedService<RabbitMqIngestionConsumer>();

builder.Services.AddControllers();

var app = builder.Build();

app.MapControllers();

// Startup connectivity validation — fail fast on infrastructure errors (FR-021, SC-004)
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var db = scope.ServiceProvider.GetRequiredService<MongoDB.Driver.IMongoDatabase>();
    await StartupValidator.ValidateMongoDbAsync(db, "NetworkA.Ingestion.API", logger);
    // RabbitMQ connectivity confirmed by RabbitMqIngestionConsumer.StartAsync
}

app.Run();
