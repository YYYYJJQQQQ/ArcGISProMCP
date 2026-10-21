namespace ArcGISProMCP.Server;

public static class ExecutionGuidance
{
    public const string Instructions = " If MCP lacks a capability, proactively use available client file/shell/browser/vision tools to finish already-authorized work, verify results and continue; do not ask merely because a method is missing. Never bypass permissions/consent or replay an uncertain GIS write. For ribbon/panels get_pro_ui -> execute_pro_ui_action -> inspect observed state/capture. Inspect actual map previews and refine visual hierarchy; layout validity is not aesthetic approval. get_execution_guidance provides fallback examples and boundaries.";
    public static object Get() => new
    {
        decision = "MCP gap -> inspect actual available tools -> choose authorized alternative -> execute -> verify -> continue user goal",
        examples = new[] {
            new { missing = "online data discovery", alternative = "available web/browser tools; download only from verified sources to an authorized NEW project path; record provenance; GIS import via MCP" },
            new { missing = "file discovery or format inspection", alternative = "available file/shell tools, bounded enumeration and read-only Python; keep workspace/permission constraints" },
            new { missing = "visual quality judgement", alternative = "export new small preview with MCP, open image using available vision/file tools, render PDF with available tools, then adjust in Pro" },
            new { missing = "Pro ribbon interaction", alternative = "get_pro_ui and supported execute_pro_ui_action; additional desktop tools only if actually enabled, not assumed" } },
        mustNotFallbackAround = new[] { "access_denied", "source overwrite/delete protection", "required human consent", "outcome_unknown", "unavailable GUI channel" },
        completion = "Report the user outcome and evidence. An unavailable MCP method alone is not a reason to ask the user to do the work.",
        visualAcceptance = "Inspect actual overview and detail images: thematic hierarchy, contextual layers, contrast, line widths, label priority and readability. No automatic aesthetic success from qualityPassed alone. Use a bounded improvement loop; disclose remaining defects.",
        limits = "Guidance influences the client agent; it cannot create missing host tools or guarantee every model invocation follows it. Test behavior using representative user requests. No recursive model/API integration or new credentials required."
    };
}
