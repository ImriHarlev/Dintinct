using Shared.Contracts.Payloads;

namespace Shared.Contracts.Models;

public class Job
{
    public string Id { get; set; } = string.Empty;
    public string ExternalId { get; set; } = string.Empty;
    public IngestionRequestPayload OriginalRequest { get; set; } = null!;
    public string Status { get; set; } = string.Empty;
    public string TemporalWorkflowId { get; set; } = string.Empty;
    public Dictionary<string, int> ChunkRetryCounters { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
