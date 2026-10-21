using System.Text.Json;

namespace ArcGISProMCP.Contracts;

public static class WorkflowProtocol
{
    public const int ToolCount = 5;
    public const int TotalMcpTools = LayoutCommands.TotalMcpTools + ToolCount + 1 + 6 + 2 + 4; // Existing74 plus UI read/action/capture and fallback guidance.
    public const int MaxSteps = 32;
    public const int MaxPlanBytes = 65536;
}

public sealed record WorkflowStep(string Id, string Tool, Dictionary<string, JsonElement> Arguments);
public sealed record WorkflowPlan(string WorkflowId, string ProjectPath, WorkflowStep[] Steps,
    int? ProcessId = null, int TimeoutSeconds = 900);

public sealed record WorkflowStepProgress(string Id, string Tool, string OperationId,
    string State = "pending", string? JobKind = null, string? JobId = null,
    DateTimeOffset? StartedAtUtc = null, DateTimeOffset? CompletedAtUtc = null,
    BridgeError? Error = null, string? ResultSha256 = null);

// Executors may report a known interruption or an unknown outcome without mislabelling it as success.
// Ordinary SDK/ArcPy executors continue to return their existing result objects unchanged.
public sealed record BackgroundJobCompletion(string State, object Result, BridgeError? Error = null);
