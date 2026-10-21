using System.ComponentModel;
using System.Text.Json;
using ArcGISProMCP.Contracts;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ArcGISProMCP.Server;

[McpServerToolType]
public sealed class ArcGisWorkflowTools(WorkflowCoordinator workflows)
{
    [McpServerTool(Name = "validate_workflow", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Preflight sequential steps before writes. Omit step projectPath/processId/operationId. Edit ref: {\"$ref\":\"addStep#/layerUri\"}; terminal export ref: {\"$ref\":\"exportStep#/result/files/0/path\"}. Validates known result fields; dynamic subtrees rechecked at execution. No save/delete/retry/scripts/forward refs.")]
    public Task<CallToolResult> Validate(WorkflowPlan plan, CancellationToken cancellationToken = default) => Reply(() => workflows.ValidateAsync(plan, cancellationToken));

    [McpServerTool(Name = "run_workflow", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Submit sequential plan; returns workflow jobId. Waits real child terminals and resolves prior results. Same ID+plan replays receipt only. Poll get_workflow; cancel explicitly. No transaction/rollback.")]
    public Task<CallToolResult> Run(WorkflowPlan plan, CancellationToken cancellationToken = default) => Reply(() => workflows.StartAsync(plan, cancellationToken));

    [McpServerTool(Name = "get_workflow", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Read persisted job/steps, child IDs, errors and timestamps. stepId returns one full result; waitSeconds0–10 reduces polling. includeMessages=false omits repeated job messages only; default true preserves full response. Cancelling this read does not cancel GIS.")]
    public Task<CallToolResult> Get(string workflowId, string? stepId = null, int waitSeconds = 0, bool includeMessages = true, CancellationToken cancellationToken = default) =>
        Reply(() => workflows.WaitAsync(workflowId, stepId, waitSeconds, cancellationToken, includeMessages));

    [McpServerTool(Name = "cancel_workflow", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Request cancellation through owning Server; stops successors, requests cooperative child cancellation. Poll true outcome; in-flight render may succeed. No rollback/deletion.")]
    public Task<CallToolResult> Cancel(string workflowId) => Reply(() => Task.FromResult(workflows.Cancel(workflowId)));

    [McpServerTool(Name = "resume_workflow", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Continue stopped workflow with NEW ID in same Pro session. Reconciles child jobs, verifies completed file hashes. Refuses unknown/failed/cancelled writes and unverified datasets; inspect before a new plan. Never resubmits old child IDs.")]
    public Task<CallToolResult> Resume(string workflowId, string newWorkflowId, CancellationToken cancellationToken = default) =>
        Reply(() => workflows.ResumeAsync(workflowId, newWorkflowId, cancellationToken));

    private static async Task<CallToolResult> Reply(Func<Task<object>> action)
    {
        try { return Result(await action()); }
        catch (BridgeException ex) { return Result(new { error = new BridgeError(ex.Code, ex.Message) }, true); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { return Result(new { error = new BridgeError("workflow_error", ex.Message) }, true); }
    }
    private static CallToolResult Result(object value, bool error = false)
    {
        var json = JsonSerializer.SerializeToElement(value, BridgeProtocol.JsonOptions);
        return new() { IsError = error, StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }] };
    }
}
