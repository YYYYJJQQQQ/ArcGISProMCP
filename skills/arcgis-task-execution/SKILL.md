---
name: arcgis-task-execution
description: Complete ArcGIS Pro GIS tasks using the MCP plus available file, Python, browser and image tools; use when a GIS task spans capabilities or needs visual map refinement, especially when an MCP function is missing. Preserve the user's requested scope and existing permissions.
---

Complete the user's outcome across the tools actually available in this session. A missing MCP method is not a reason to hand the task back to the user.

When blocked by a capability gap, inspect available tool descriptions, identify an authorized alternative, and execute it without requesting permission already granted. File search/inspection and bounded Python processing belong to file/shell tools; source discovery belongs to available web/browser tools; ArcGIS operations belong to the GIS MCP; image inspection and PDF rendering belong to available visual/file tools. Do not invent enabled desktop access or credentials. Invoke `get_execution_guidance` for concrete fallback examples when needed.

Distinguish a missing capability from a safety refusal. Do not route around an unauthorized path, source-data overwrite/delete restriction, human-confirmation requirement, or uncertain GIS write by using shell, ArcPy or UI clicks. Inspect the existing result before an uncertain retry. Ask only when required input, authorization or a real environment blocker remains after safe alternatives.

Read health/current project and exact URIs before GIS work. Use current PID when needed and the project's established task terminal/operationId rules. For toolbar or panel tasks, read `get_pro_ui`; query relevant command IDs if discovery is incomplete. Use `execute_pro_ui_action` for supported actions and inspect observed active tool/tab/pane afterward. A dispatched command or opened dialog does not prove the requested GIS task completed. Window screenshots and map/layout exports have different purposes; never substitute a map image for a claim about visible ribbon/dialog content.

For map design, establish purpose, scale, theme and contextual layer roles. Reuse original symbology when its categories carry meaning; request recoloring only explicitly. Tune water/building/road/vegetation/boundary hierarchy, stroke weights, label sizes and halos using real data. Do not invent terrain, shadows or smoothing that would misrepresent geography. Thematic `options.visualStyle` applies to copied layers, preserving source maps and data.

Export a small overview plus detail at the final intended scale and actually inspect both with available image tools. Judge topic prominence, contextual restraint, color coherence, road/water continuity, label legibility and collisions. Refine up to three bounded passes when useful; keep prior previews and report remaining defects instead of declaring visual success from a structural `qualityPassed` flag. Final export follows visual review; verify output hashes and source protection.

Record fallback tool choice, result evidence and any true blocker. Do not claim universal autonomous recovery, full UI coverage or aesthetic quality without the corresponding observations.
