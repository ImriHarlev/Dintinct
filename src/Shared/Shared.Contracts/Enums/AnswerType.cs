using System.Text.Json.Serialization;

namespace Shared.Contracts.Enums;

public enum AnswerType
{
    [JsonStringEnumMemberName("RABBIT_MQ")]
    RabbitMQ,
    [JsonStringEnumMemberName("FILE_SYSTEM")]
    FileSystem
}
