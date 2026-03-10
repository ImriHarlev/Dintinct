using Shared.Contracts.Payloads;

namespace Shared.Contracts.Interfaces;

public interface IUpdateClientAActivities
{
    Task UpdateClientAAsync(StatusCallbackPayload payload);
    Task NotifyManifestFailureAsync(string origJobId);
}
