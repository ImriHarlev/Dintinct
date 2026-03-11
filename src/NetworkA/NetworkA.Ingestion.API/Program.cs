using FluentValidation;
using NetworkA.Ingestion.API.Interfaces;
using NetworkA.Ingestion.API.Services;
using NetworkA.Ingestion.API.Validators;
using Serilog;
using Shared.Contracts.Interfaces;
using Shared.Contracts.Payloads;
using Shared.Infrastructure.Extensions;
using Shared.Infrastructure.Options;
using Shared.Infrastructure.Repositories;
using Shared.Infrastructure.Startup;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, config) => config
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.WithProperty("Service", "NetworkA.Ingestion.API")
    .WriteTo.Console());


builder.Services.Configure<MongoDbOptions>(builder.Configuration.GetSection("MongoDB"));
builder.Services.Configure<TemporalOptions>(builder.Configuration.GetSection("Temporal"));
builder.Services.AddMongoDb();
builder.Services.AddTemporalClient();

builder.Services.AddScoped<IJobRepository, MongoJobRepository>();
builder.Services.AddScoped<IIngestionService, IngestionService>();
builder.Services.AddScoped<IValidator<IngestionRequestPayload>, IngestionRequestValidator>();

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

var app = builder.Build();

app.MapControllers();

app.Run();
