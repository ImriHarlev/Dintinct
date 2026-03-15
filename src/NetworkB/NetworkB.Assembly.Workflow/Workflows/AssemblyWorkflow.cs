using Shared.Contracts.Enums;
using Shared.Contracts.Models;
using Shared.Contracts.Payloads;
using Shared.Contracts.Signals;
using Shared.Infrastructure.Activities;
using Shared.Infrastructure.Extensions;
using Temporalio.Workflows;
using TemporalWorkflow = Temporalio.Workflows.Workflow;

namespace NetworkB.Assembly.Workflow.Workflows;

[Workflow]
public class AssemblyWorkflow
{
    private readonly List<string> _receivedChunkPaths = new();
    private readonly List<string> _hardFailedChunkNames = new();
    private readonly List<string> _unsupportedChunkNames = new();
    private bool _manifestReceived;
    private bool _manifestHardFailed;
    private int _expectedChunks;
    private string _manifestFilePath = string.Empty;
    private WorkflowActivityConfig _activityConfig = null!;

    [WorkflowRun]
    public async Task RunAsync(int timeoutMinutes)
    {
        _activityConfig = await TemporalWorkflow.ExecuteLocalActivityAsync(
            (WorkflowActivityConfigLocalActivity a) => a.FetchAsync("assembly-workflow"),
            new LocalActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(10) });

        // Wait for manifest to arrive — chunks may arrive first (Temporal buffers all signals).
        // Also unblock if the manifest itself hard-failed so we don't hang forever.
        await TemporalWorkflow.WaitConditionAsync(() => _manifestReceived || _manifestHardFailed);

        if (_manifestHardFailed)
        {
            var origJobId = TemporalWorkflow.Info.WorkflowId.Replace("assembly-", "");
            await TemporalWorkflow.ExecuteActivityAsync(
                "NotifyManifestFailure",
                [origJobId],
                GetOptions("NotifyManifestFailure", "callback-dispatch-tasks"));
            return;
        }

        var blueprint = await TemporalWorkflow.ExecuteActivityAsync<AssemblyBlueprint>(
            "ParseAndPersistManifest",
            [_manifestFilePath],
            GetOptions("ParseAndPersistManifest", "manifest-assembly-tasks"));

        _expectedChunks = blueprint.TotalChunks;

        // Wait for all chunk slots to resolve (received, hard-failed, or unsupported)
        var assemblyTimeout = TimeSpan.FromMinutes(timeoutMinutes);
        var allArrived = await TemporalWorkflow.WaitConditionAsync(
            () => _receivedChunkPaths.Count + _hardFailedChunkNames.Count + _unsupportedChunkNames.Count >= _expectedChunks,
            assemblyTimeout);

        blueprint.ReceivedChunkNames = new HashSet<string>(_receivedChunkPaths.Select(p => Path.GetFileName(p)!));
        blueprint.HardFailedChunkNames = new HashSet<string>(_hardFailedChunkNames);
        blueprint.UnsupportedChunkNames = new HashSet<string>(_unsupportedChunkNames);

        var fileResults = await TemporalWorkflow.ExecuteActivityAsync<IReadOnlyList<FileResult>>(
            "AssembleAndValidate",
            [blueprint, _receivedChunkPaths],
            GetOptions("AssembleAndValidate", "heavy-assembly-tasks"));

        JobStatus finalStatus;
        if (!allArrived)
        {
            finalStatus = JobStatus.Timeout;
        }
        else if (fileResults.All(r => r.Status == FileTransferStatus.Completed))
        {
            finalStatus = JobStatus.Completed;
        }
        else if (fileResults.Any(r => r.Status == FileTransferStatus.Completed))
        {
            finalStatus = JobStatus.CompletedPartially;
        }
        else
        {
            finalStatus = JobStatus.Failed;
        }

        blueprint.Status = finalStatus.ToString();

        await TemporalWorkflow.ExecuteActivityAsync(
            "UpdateBlueprintStatus",
            [blueprint],
            GetOptions("UpdateBlueprintStatus", "manifest-assembly-tasks"));

        await TemporalWorkflow.ExecuteActivityAsync(
            "WriteCsvReport",
            [blueprint, fileResults],
            GetOptions("WriteCsvReport", "callback-dispatch-tasks"));

        var payload = await TemporalWorkflow.ExecuteActivityAsync<StatusCallbackPayload>(
            "DispatchAnswer",
            [blueprint, fileResults, finalStatus],
            GetOptions("DispatchAnswer", "callback-dispatch-tasks"));

        await TemporalWorkflow.ExecuteActivityAsync(
            "UpdateClientA",
            [payload],
            GetOptions("UpdateClientA", "callback-dispatch-tasks"));
    }

    [WorkflowSignal]
    public async Task ManifestArrivedAsync(ManifestSignal signal)
    {
        _manifestFilePath = signal.FilePath;
        _manifestReceived = true;
        await Task.CompletedTask;
    }

    [WorkflowSignal]
    public async Task ChunkArrivedAsync(ChunkSignal signal)
    {
        _receivedChunkPaths.Add(signal.FilePath);
        await Task.CompletedTask;
    }

    [WorkflowSignal]
    public async Task UnsupportedFileAsync(UnsupportedFileSignal signal)
    {
        var fileName = Path.GetFileName(signal.FilePath);
        _unsupportedChunkNames.Add(fileName);
        await Task.CompletedTask;
    }

    [WorkflowSignal]
    public async Task HardFailAsync(HardFailSignal signal)
    {
        if (signal.ChunkName.EndsWith("_manifest.json", StringComparison.OrdinalIgnoreCase))
            _manifestHardFailed = true;
        else
            _hardFailedChunkNames.Add(signal.ChunkName);
        await Task.CompletedTask;
    }

    private ActivityOptions GetOptions(string activityName, string taskQueue) =>
        _activityConfig.ToActivityOptions(activityName, taskQueue);
}
