using Shared.Contracts.Models;

namespace Shared.Contracts.Interfaces;

public interface IJobSetupActivities
{
    Task<WorkflowConfiguration> FetchConfigurationAsync(string jobId);
}
