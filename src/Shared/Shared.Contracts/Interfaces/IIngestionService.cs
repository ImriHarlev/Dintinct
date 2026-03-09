using Shared.Contracts.Payloads;

namespace Shared.Contracts.Interfaces;

public interface IIngestionService
{
    Task<string> StartJobAsync(IngestionRequestPayload request, CancellationToken ct = default);
}
