using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Shared.Contracts.Models;

namespace Shared.Infrastructure.Repositories;

public class MongoWorkflowActivityConfigRepository : IWorkflowActivityConfigRepository
{
    private readonly IMongoCollection<WorkflowActivityConfigDocument> _collection;

    public MongoWorkflowActivityConfigRepository(IMongoDatabase database)
    {
        _collection = database.GetCollection<WorkflowActivityConfigDocument>("workflow_activity_configs");
    }

    public async Task<WorkflowActivityConfig?> GetByWorkflowKeyAsync(string workflowKey, CancellationToken ct = default)
    {
        var filter = Builders<WorkflowActivityConfigDocument>.Filter.Eq(d => d.WorkflowKey, workflowKey);
        var doc = await _collection.Find(filter).FirstOrDefaultAsync(ct);
        return doc is null ? null : doc.ToModel();
    }

    private sealed class WorkflowActivityConfigDocument
    {
        [BsonId]
        public string WorkflowKey { get; set; } = string.Empty;

        public Dictionary<string, ActivityTimeoutConfigDocument> Activities { get; set; } = new();

        public WorkflowActivityConfig ToModel() =>
            new(WorkflowKey, Activities.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.ToModel()));
    }

    private sealed class ActivityTimeoutConfigDocument
    {
        public int StartToCloseMinutes { get; set; }
        public int? ScheduleToCloseMinutes { get; set; }
        public int? HeartbeatSeconds { get; set; }
        public RetryPolicyConfigDocument? RetryPolicy { get; set; }

        public ActivityTimeoutConfig ToModel() =>
            new(StartToCloseMinutes, ScheduleToCloseMinutes, HeartbeatSeconds, RetryPolicy?.ToModel());
    }

    private sealed class RetryPolicyConfigDocument
    {
        public int InitialIntervalSeconds { get; set; }
        public double BackoffCoefficient { get; set; }
        public int MaximumIntervalSeconds { get; set; }
        public int MaximumAttempts { get; set; }
        public List<string> NonRetryableErrorTypes { get; set; } = [];

        public RetryPolicyConfig ToModel() =>
            new(InitialIntervalSeconds, BackoffCoefficient, MaximumIntervalSeconds, MaximumAttempts, NonRetryableErrorTypes);
    }
}
