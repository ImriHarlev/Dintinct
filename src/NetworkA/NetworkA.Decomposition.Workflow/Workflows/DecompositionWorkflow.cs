using Shared.Contracts.Models;
using Shared.Contracts.Payloads;
using Shared.Infrastructure.Activities;
using Shared.Infrastructure.Extensions;
using Temporalio.Workflows;
using TemporalWorkflow = Temporalio.Workflows.Workflow;

namespace NetworkA.Decomposition.Workflow.Workflows;

[Workflow]
public class DecompositionWorkflow
{
    private bool _callbackReceived;
    private readonly Dictionary<string, int> _chunkRetryCounts = new();
    private WorkflowConfiguration _config = null!;
    private WorkflowActivityConfig _activityConfig = null!;

    [WorkflowRun]
    public async Task RunAsync(IngestionRequestPayload request)
    {
        _activityConfig = await TemporalWorkflow.ExecuteLocalActivityAsync(
            (WorkflowActivityConfigLocalActivity a) => a.FetchAsync("decomposition-workflow"),
            new LocalActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(10) });

        var jobId = TemporalWorkflow.Info.WorkflowId.Replace("decomposition-", "");

        _config = await TemporalWorkflow.ExecuteActivityAsync<WorkflowConfiguration>(
            "FetchConfiguration",
            [jobId],
            GetOptions("FetchConfiguration", "setup-tasks"));

        var prepared = await TemporalWorkflow.ExecuteActivityAsync<PreparedSource>(
            "PrepareSource",
            [_config],
            GetOptions("PrepareSource", "heavy-processing-tasks"));

        var metadata = await TemporalWorkflow.ExecuteActivityAsync<DecompositionMetadata>(
            "DecomposeAndSplit",
            [prepared, _config],
            GetOptions("DecomposeAndSplit", "heavy-processing-tasks"));

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
            [enrichedMetadata],
            GetOptions("WriteManifest", "manifest-tasks"));

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
                [jobId, chunkName],
                GetOptions("RetryChunk", "retry-dispatch-tasks"));
        }
        else
        {
            await TemporalWorkflow.ExecuteActivityAsync(
                "WriteHardFail",
                [jobId, chunkName],
                GetOptions("WriteHardFail", "retry-dispatch-tasks"));
        }
    }

    private ActivityOptions GetOptions(string activityName, string taskQueue) =>
        _activityConfig.ToActivityOptions(activityName, taskQueue);
}
