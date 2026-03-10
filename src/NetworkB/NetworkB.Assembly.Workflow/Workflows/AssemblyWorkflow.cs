using Shared.Contracts.Enums;
using Shared.Contracts.Interfaces;
using Shared.Contracts.Models;
using Shared.Contracts.Payloads;
using Shared.Contracts.Signals;
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

    [WorkflowRun]
    public async Task RunAsync(int timeoutMinutes)
    {
        // Wait for manifest to arrive — chunks may arrive first (Temporal buffers all signals).
        // Also unblock if the manifest itself hard-failed so we don't hang forever.
        await TemporalWorkflow.WaitConditionAsync(() => _manifestReceived || _manifestHardFailed);

        if (_manifestHardFailed)
        {
            var origJobId = TemporalWorkflow.Info.WorkflowId.Replace("assembly-", "");
            await TemporalWorkflow.ExecuteActivityAsync(
                "NotifyManifestFailure",
                new object[] { origJobId },
                new ActivityOptions { TaskQueue = "callback-dispatch-tasks", StartToCloseTimeout = TimeSpan.FromMinutes(10) });
            return;
        }

        var blueprint = await TemporalWorkflow.ExecuteActivityAsync<AssemblyBlueprint>(
            "ParseAndPersistManifest",
            new object[] { _manifestFilePath },
            new ActivityOptions { TaskQueue = "manifest-assembly-tasks", StartToCloseTimeout = TimeSpan.FromMinutes(5) });

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
            new object[] { blueprint, (IReadOnlyList<string>)_receivedChunkPaths },
            new ActivityOptions { TaskQueue = "heavy-assembly-tasks", StartToCloseTimeout = TimeSpan.FromMinutes(30) });

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
            new object[] { blueprint },
            new ActivityOptions { TaskQueue = "manifest-assembly-tasks", StartToCloseTimeout = TimeSpan.FromMinutes(5) });

        await TemporalWorkflow.ExecuteActivityAsync(
            "WriteCsvReport",
            new object[] { blueprint, fileResults },
            new ActivityOptions { TaskQueue = "callback-dispatch-tasks", StartToCloseTimeout = TimeSpan.FromMinutes(10) });

        var payload = await TemporalWorkflow.ExecuteActivityAsync<StatusCallbackPayload>(
            "DispatchAnswer",
            new object[] { blueprint, fileResults, finalStatus },
            new ActivityOptions { TaskQueue = "callback-dispatch-tasks", StartToCloseTimeout = TimeSpan.FromMinutes(10) });

        await TemporalWorkflow.ExecuteActivityAsync(
            "UpdateClientA",
            new object[] { payload },
            new ActivityOptions { TaskQueue = "callback-dispatch-tasks", StartToCloseTimeout = TimeSpan.FromMinutes(10) });
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
}
