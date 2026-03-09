using Shared.Contracts.Models;
using Shared.Contracts.Payloads;
using Temporalio.Workflows;
using TemporalWorkflow = Temporalio.Workflows.Workflow;

namespace NetworkA.Decomposition.Workflow.Workflows;

[Workflow]
public class DecompositionWorkflow
{
    private bool _callbackReceived;
    private readonly Dictionary<string, int> _chunkRetryCounts = new();
    private WorkflowConfiguration _config = null!;

    [WorkflowRun]
    public async Task RunAsync(IngestionRequestPayload request)
    {
        var jobId = TemporalWorkflow.Info.WorkflowId.Replace("decomposition-", "");

        _config = await TemporalWorkflow.ExecuteActivityAsync<WorkflowConfiguration>(
            "FetchConfiguration",
            new object[] { jobId },
            new ActivityOptions { TaskQueue = "setup-tasks", StartToCloseTimeout = TimeSpan.FromMinutes(5) });

        var metadata = await TemporalWorkflow.ExecuteActivityAsync<DecompositionMetadata>(
            "DecomposeAndSplit",
            new object[] { _config },
            new ActivityOptions { TaskQueue = "heavy-processing-tasks", StartToCloseTimeout = TimeSpan.FromMinutes(30) });

        var enrichedMetadata = metadata with
        {
            AnswerType = request.AnswerType,
            AnswerLocation = request.AnswerLocation,
            TargetNetwork = request.TargetNetwork,
            CallingSystemId = request.CallingSystemId,
            CallingSystemName = request.CallingSystemName,
            ExternalId = request.ExternalId
        };

        await TemporalWorkflow.ExecuteActivityAsync(
            "WriteManifest",
            new object[] { enrichedMetadata },
            new ActivityOptions { TaskQueue = "manifest-tasks", StartToCloseTimeout = TimeSpan.FromMinutes(5) });

        await TemporalWorkflow.WaitConditionAsync(() => _callbackReceived);
    }

    [WorkflowSignal]
    public async Task FinalStatusReceivedAsync(StatusCallbackPayload payload)
    {
        _callbackReceived = true;
        await Task.CompletedTask;
    }

    [WorkflowSignal]
    public async Task ChunkRetryRequestedAsync(string chunkName)
    {
        if (!_chunkRetryCounts.ContainsKey(chunkName))
            _chunkRetryCounts[chunkName] = 0;

        _chunkRetryCounts[chunkName]++;
        var jobId = TemporalWorkflow.Info.WorkflowId.Replace("decomposition-", "");

        if (_chunkRetryCounts[chunkName] <= _config.MaxRetryCount)
        {
            await TemporalWorkflow.ExecuteActivityAsync(
                "RetryChunk",
                new object[] { jobId, chunkName },
                new ActivityOptions { TaskQueue = "retry-dispatch-tasks", StartToCloseTimeout = TimeSpan.FromMinutes(5) });
        }
        else
        {
            await TemporalWorkflow.ExecuteActivityAsync(
                "WriteHardFail",
                new object[] { jobId, chunkName },
                new ActivityOptions { TaskQueue = "retry-dispatch-tasks", StartToCloseTimeout = TimeSpan.FromMinutes(5) });
        }
    }
}
