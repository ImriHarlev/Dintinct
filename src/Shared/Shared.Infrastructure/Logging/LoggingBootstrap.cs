using Serilog;
using Serilog.Events;

namespace Shared.Infrastructure.Logging;

public static class LoggingBootstrap
{
    private const string OutputTemplate =
        "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}] [{Service}] {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// Adds a rolling daily file sink to the logger configuration.
    /// Files are written to a centralized logs folder at the solution root.
    /// The path is calculated relative to the current directory by traversing up to find the solution root.
    /// Logs are retained for 7 days, with a 100 MB per-file size cap.
    /// </summary>
    public static LoggerConfiguration WithFileLogging(
        this LoggerConfiguration config,
        string serviceName,
        LogEventLevel minimumLevel = LogEventLevel.Information)
    {
        var logPath = GetCentralizedLogPath(serviceName);

        return config.WriteTo.File(
            path: logPath,
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 7,
            fileSizeLimitBytes: 100 * 1024 * 1024,
            rollOnFileSizeLimit: true,
            outputTemplate: OutputTemplate,
            restrictedToMinimumLevel: minimumLevel);
    }

    private static string GetCentralizedLogPath(string serviceName)
    {
        // Start from the current directory
        var currentDir = Directory.GetCurrentDirectory();
        var searchDir = new DirectoryInfo(currentDir);

        // Traverse up to find the solution root (Dintinct folder)
        while (searchDir != null && !searchDir.Name.Equals("Dintinct", StringComparison.OrdinalIgnoreCase))
        {
            searchDir = searchDir.Parent;
        }

        // If we found Dintinct, use it; otherwise fall back to current directory
        var logRoot = searchDir?.FullName ?? currentDir;
        var logsFolder = Path.Combine(logRoot, "logs");

        // Ensure the logs directory exists
        Directory.CreateDirectory(logsFolder);

        return Path.Combine(logsFolder, $"{serviceName}-.log");
    }
}
