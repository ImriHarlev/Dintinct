using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Shared.Infrastructure.Extensions;

public static class SharedConfigurationExtensions
{
    /// <summary>
    /// Loads shared configuration files from the solution's src/Shared directory
    /// (resolved via the DINTINCT_ROOT environment variable or directory traversal).
    /// Currently loads: proxy-config.json
    /// </summary>
    public static IHostApplicationBuilder AddSharedConfiguration(
        this IHostApplicationBuilder builder)
    {
        var dintinctRoot = Environment.GetEnvironmentVariable("DINTINCT_ROOT")
            ?? FindDintinctRoot(Directory.GetCurrentDirectory())
            ?? Directory.GetCurrentDirectory();

        var sharedDir = Path.Combine(dintinctRoot, "src", "Shared");

        var proxyConfigPath = Path.Combine(sharedDir, "proxy-config.json");
        if (File.Exists(proxyConfigPath))
            builder.Configuration.AddJsonFile(proxyConfigPath, optional: false, reloadOnChange: true);

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
