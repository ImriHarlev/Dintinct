using Shared.Contracts.Payloads;

namespace Shared.Contracts.Interfaces;

public interface IAnswerDispatcher
{
    Task DispatchAsync(StatusCallbackPayload payload, CancellationToken ct = default);
}
