using Elasticsearch.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Sinks.Elasticsearch;

namespace Shared.Infrastructure.Logging;

public static class SerilogExtensions
{
    /// <summary>
    /// Configures Serilog by loading the centralized logging.json from the Dintinct solution
    /// root (resolved via the DINTINCT_ROOT environment variable or directory traversal).
    /// Placeholders %DINTINCT_ROOT% and %SERVICE_NAME% are expanded before Serilog reads the
    /// configuration. Each service identifies itself via "ServiceName" in its own appsettings.json.
    /// </summary>
    public static IHostApplicationBuilder AddSerilogFromConfiguration(
        this IHostApplicationBuilder builder)
    {
        var dintinctRoot = Environment.GetEnvironmentVariable("DINTINCT_ROOT")
            ?? FindDintinctRoot(Directory.GetCurrentDirectory())
            ?? Directory.GetCurrentDirectory();

        var sharedDir = Path.Combine(dintinctRoot, "src", "Shared");

        var loggingConfigPath = Path.Combine(sharedDir, "logging.json");
        if (File.Exists(loggingConfigPath))
            builder.Configuration.AddJsonFile(loggingConfigPath, optional: false, reloadOnChange: true);

        Directory.CreateDirectory(Path.Combine(dintinctRoot, "logs"));

        var serviceName = builder.Configuration["ServiceName"] ?? "Unknown";

        foreach (var item in builder.Configuration.AsEnumerable())
        {
            if (item.Value is null) continue;
            var expanded = item.Value
                .Replace("%DINTINCT_ROOT%", dintinctRoot)
                .Replace("%SERVICE_NAME%", serviceName);
            if (expanded != item.Value)
                builder.Configuration[item.Key] = expanded;
        }

        builder.Configuration["Serilog:Properties:Service"] = serviceName;

        // Build the logger immediately so it captures the fully-expanded configuration.
        // A deferred lambda risks being evaluated before the in-memory overrides are visible,
        // which causes silent failures (no sinks) on WebApplicationBuilder hosts.
        var loggerConfig = new LoggerConfiguration()
            .ReadFrom.Configuration(builder.Configuration);

        // The sink is only added when both are set, so services start cleanly when no Elasticsearch node is running.
        var esUri = builder.Configuration["Elasticsearch:Uri"];
        var esEnabled = builder.Configuration.GetValue<bool>("Elasticsearch:Enabled");

        if (esEnabled && !string.IsNullOrWhiteSpace(esUri))
        {
            // When Elasticsearch is enabled, Serilog.Debugging.SelfLog is turned on and writes to Console.Error. 
            // This surfaces connection failures, serialization errors, and rejected batches that would otherwise be silently swallowed.
            Serilog.Debugging.SelfLog.Enable(
                msg => Console.Error.WriteLine("[Serilog:ES] " + msg));

            var indexFormat = builder.Configuration["Elasticsearch:IndexFormat"]
                ?? "dintinct-logs-{0:yyyy.MM}";

            loggerConfig = loggerConfig.WriteTo.Elasticsearch(
                new ElasticsearchSinkOptions(new Uri(esUri))
                {
                    IndexFormat = indexFormat,
                    AutoRegisterTemplate = true,
                    AutoRegisterTemplateVersion = AutoRegisterTemplateVersion.ESv8,
                    BatchPostingLimit = 50,
                    // any event the Elasticsearch sink can't deliver is written to SelfLog rather than thrown or dropped silently, so you always see what's being lost.
                    EmitEventFailure = EmitEventFailureHandling.WriteToSelfLog,
                });
        }

        var serilogLogger = loggerConfig.CreateLogger();

        Log.Logger = serilogLogger;
        builder.Services.AddSerilog(serilogLogger, dispose: true);
        return builder;
    }

    private static string? FindDintinctRoot(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir != null && !dir.Name.Equals("Dintinct", StringComparison.OrdinalIgnoreCase))
            dir = dir.Parent;
        return dir?.FullName;
    }
}
