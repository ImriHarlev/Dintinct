using FluentValidation;
using NetworkA.Ingestion.API.Interfaces;
using NetworkA.Ingestion.API.Services;
using NetworkA.Ingestion.API.Validators;
using Shared.Contracts.Payloads;
using Shared.Infrastructure.Extensions;
using Shared.Infrastructure.Logging;
using Shared.Infrastructure.Options;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);
builder.AddSerilogFromConfiguration();

builder.Services.Configure<TemporalOptions>(builder.Configuration.GetSection("Temporal"));
builder.Services.AddTemporalClient();

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
