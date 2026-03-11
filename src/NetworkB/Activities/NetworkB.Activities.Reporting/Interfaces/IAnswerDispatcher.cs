using Shared.Contracts.Payloads;

namespace NetworkB.Activities.Reporting.Interfaces;

public interface IAnswerDispatcher
{
    Task DispatchAsync(StatusCallbackPayload payload, CancellationToken ct = default);
}
