using NetworkB.Assembly.Workflow.Activities;
using Shared.Contracts.Enums;
using Shared.Contracts.Models;
using Shared.Contracts.Signals;
using Shared.Infrastructure.Extensions;

using Temporalio.Workflows;
using TemporalWorkflow = Temporalio.Workflows.Workflow;

namespace NetworkB.Assembly.Workflow.Workflows;

[Workflow]
public class AssemblyWorkflow
{
    private AssemblyRuntimeConfig _runtimeConfig = null!;
    private readonly List<string> _receivedChunkPaths = new();
    private readonly List<string> _hardFailedChunkNames = new();
    private readonly List<string> _unsupportedChunkNames = new();
    private bool _manifestReceived;
    private bool _manifestHardFailed;
    private int _expectedChunks;
    private string _manifestFilePath = string.Empty;

    [WorkflowRun]
    public async Task RunAsync(int timeoutMinutes)
    {
        _runtimeConfig = await TemporalWorkflow.ExecuteLocalActivityAsync(
            (AssemblyConfigLocalActivity a) => a.FetchAsync(),
            new LocalActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30) });

        // Wait for manifest to arrive — chunks may arrive first (Temporal buffers all signals).
        // Also unblock if the manifest itself hard-failed so we don't hang forever.
        await TemporalWorkflow.WaitConditionAsync(() => _manifestReceived || _manifestHardFailed);

        if (_manifestHardFailed)
            return;

        var blueprint = await TemporalWorkflow.ExecuteActivityAsync<AssemblyBlueprint>(
            "ParseManifest",
            [_manifestFilePath],
            GetOptions("ParseManifest", "manifest-assembly-tasks"));

        _expectedChunks = blueprint.TotalChunks;

        // Wait for all chunk slots to resolve (received, hard-failed, or unsupported)
        var assemblyTimeout = TimeSpan.FromMinutes(timeoutMinutes);
        var allArrived = await TemporalWorkflow.WaitConditionAsync(
            () => _receivedChunkPaths.Count + _hardFailedChunkNames.Count + _unsupportedChunkNames.Count >= _expectedChunks,
            assemblyTimeout);

        blueprint.ReceivedChunkNames = new HashSet<string>(_receivedChunkPaths.Select(p => Path.GetFileName(p)!));
        blueprint.HardFailedChunkNames = new HashSet<string>(_hardFailedChunkNames);
        blueprint.UnsupportedChunkNames = new HashSet<string>(_unsupportedChunkNames);

        var assembleResult = await TemporalWorkflow.ExecuteActivityAsync<AssembleFilesResult>(
            "AssembleFiles",
            [blueprint, _receivedChunkPaths],
            GetOptions("AssembleFiles", "heavy-assembly-tasks"));

        await TemporalWorkflow.ExecuteActivityAsync(
            "RepackAndFinalize",
            [blueprint],
            GetOptions("RepackAndFinalize", "heavy-assembly-tasks"));

        var fileResults = assembleResult.FileResults;

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
            "WriteCsvReport",
            [blueprint, fileResults],
            GetOptions("WriteCsvReport", "callback-dispatch-tasks"));

        await TemporalWorkflow.ExecuteActivityAsync(
            "DispatchAnswer",
            [blueprint, fileResults, finalStatus],
            GetOptions("DispatchAnswer", "callback-dispatch-tasks"));
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
        _runtimeConfig.ActivityConfig.Activities[activityName].ToActivityOptions(taskQueue);
}
