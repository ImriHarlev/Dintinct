using NetworkA.Decomposition.Workflow.Activities;
using Shared.Contracts.Models;
using Shared.Contracts.Payloads;
using Shared.Infrastructure.Extensions;
using Temporalio.Workflows;
using TemporalWorkflow = Temporalio.Workflows.Workflow;

namespace NetworkA.Decomposition.Workflow.Workflows;

[Workflow]
public class DecompositionWorkflow
{
    private DecompositionRuntimeConfig _runtimeConfig = null!;
    private WorkflowConfiguration _config = null!;

    private string JobId => TemporalWorkflow.Info.WorkflowId.Replace("decomposition-", "");

    [WorkflowRun]
    public async Task RunAsync(IngestionRequestPayload request)
    {
        _runtimeConfig = await TemporalWorkflow.ExecuteLocalActivityAsync(
            (DecompositionConfigLocalActivity a) => a.FetchAsync(),
            new LocalActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30) });

        _config = new WorkflowConfiguration(
            JobId: JobId,
            SourcePath: request.SourcePath,
            TargetPath: request.TargetPath,
            ProxyRules: _runtimeConfig.ProxyRules);

        var prepared = await TemporalWorkflow.ExecuteActivityAsync<PreparedSource>(
            "PrepareSource",
            [JobId, _config.SourcePath],
            GetOptions("PrepareSource", "heavy-processing-tasks"));

        var splitResult = await TemporalWorkflow.ExecuteActivityAsync<SplitResult>(
            "DecomposeAndSplit",
            [prepared, _config],
            GetOptions("DecomposeAndSplit", "heavy-processing-tasks"));

        var manifest = new DecompositionMetadata(
            JobId: JobId,
            PackageType: splitResult.PackageType,
            OriginalPackageName: splitResult.OriginalPackageName,
            SourcePath: _config.SourcePath,
            TargetPath: _config.TargetPath,
            TargetNetwork: request.TargetNetwork,
            CallingSystemId: request.CallingSystemId,
            CallingSystemName: request.CallingSystemName,
            ExternalId: request.ExternalId,
            TotalChunks: splitResult.TotalChunks,
            AnswerType: request.AnswerType,
            AnswerLocation: request.AnswerLocation,
            Files: splitResult.Files,
            NestedArchives: splitResult.NestedArchives);

        await TemporalWorkflow.ExecuteActivityAsync(
            "WriteManifest",
            [manifest],
            GetOptions("WriteManifest", "manifest-tasks"));
    }

    private ActivityOptions GetOptions(string activityName, string taskQueue) =>
        _runtimeConfig.ActivityConfig.Activities[activityName].ToActivityOptions(taskQueue);
}
