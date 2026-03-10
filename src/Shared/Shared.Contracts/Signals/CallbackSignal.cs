using Shared.Contracts.Payloads;

namespace Shared.Contracts.Signals;

public record CallbackSignal(StatusCallbackPayload Payload);
