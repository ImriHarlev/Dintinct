using Microsoft.Extensions.Logging;

namespace Shared.Infrastructure.Startup;

public static class StartupValidator
{
    public static void LogTemporalWorkerRegistered(string serviceName, string taskQueue, ILogger logger)
    {
        logger.LogInformation("{ServiceName}: Temporal worker registered on queue {TaskQueue}", serviceName, taskQueue);
    }
}
