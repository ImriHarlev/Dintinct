using Shared.Contracts.Payloads;

namespace NetworkB.Activities.Reporting.Interfaces;

public interface INetworkAClient
{
    Task SendFinalStatusAsync(StatusCallbackPayload payload, CancellationToken ct = default);
    Task SendRetryRequestAsync(string origJobId, string chunkName, CancellationToken ct = default);
}
