using Shared.Contracts.Payloads;

namespace NetworkA.Callback.Receiver.Interfaces;

public interface ICallbackService
{
    Task HandleFinalStatusAsync(StatusCallbackPayload payload, CancellationToken ct = default);
    Task HandleChunkRetryRequestAsync(string origJobId, string chunkName, CancellationToken ct = default);
}
