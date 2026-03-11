using Shared.Contracts.Payloads;

namespace NetworkA.Ingestion.API.Interfaces;

public interface IIngestionService
{
    Task<string> StartJobAsync(IngestionRequestPayload request, CancellationToken ct = default);
}
