using System.Text.Json.Serialization;

namespace Shared.Contracts.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JobStatus
{
    [JsonStringEnumMemberName("COMPLETED")]
    Completed,

    [JsonStringEnumMemberName("COMPLETED_PARTIALLY")]
    CompletedPartially,

    [JsonStringEnumMemberName("FAILED")]
    Failed,

    [JsonStringEnumMemberName("TIMEOUT")]
    Timeout,

    [JsonStringEnumMemberName("INTERNAL_ERROR")]
    InternalError
}
