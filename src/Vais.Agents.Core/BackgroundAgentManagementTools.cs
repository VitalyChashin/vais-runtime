// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents.Core;

/// <summary>
/// Factory that produces the three generic management tools for background agent
/// sub-runs: <c>list_background_agents</c>, <c>view_background_agent</c>, and
/// <c>cancel_background_agent</c>. All three are backed by a single
/// <see cref="IBackgroundAgentTracker"/> instance.
/// </summary>
public static class BackgroundAgentManagementTools
{
    private static readonly JsonElement s_emptySchema = JsonDocument.Parse(
        """{"type":"object","properties":{}}""").RootElement.Clone();

    private static readonly JsonElement s_handleSchema = JsonDocument.Parse(
        """{"type":"object","properties":{"handle":{"type":"string","description":"Handle returned by the background sub-agent run tool."}},"required":["handle"]}""")
        .RootElement.Clone();

    /// <summary>
    /// Create the three management tools for the given <paramref name="tracker"/>.
    /// Returns them in the order: list, view, cancel.
    /// </summary>
    public static IReadOnlyList<ITool> Create(IBackgroundAgentTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        return
        [
            new ListTool(tracker),
            new ViewTool(tracker),
            new CancelTool(tracker),
        ];
    }

    // ── list_background_agents ───────────────────────────────────────────────

    private sealed class ListTool(IBackgroundAgentTracker tracker) : ITool
    {
        public string Name => "list_background_agents";
        public string Description => "List all background agent sub-runs started in the current coordinator run.";
        public JsonElement ParametersSchema => s_emptySchema;

        public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default)
        {
            var parentRunId = new AsyncLocalAgentContextAccessor().Current.RunId ?? "no-run";
            var records = await tracker.ListAsync(parentRunId, cancellationToken);
            return JsonSerializer.Serialize(records);
        }
    }

    // ── view_background_agent ────────────────────────────────────────────────

    private sealed class ViewTool(IBackgroundAgentTracker tracker) : ITool
    {
        public string Name => "view_background_agent";
        public string Description => "Get the current status and result of a background sub-agent run by handle.";
        public JsonElement ParametersSchema => s_handleSchema;

        public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default)
        {
            var handle = ExtractHandle(arguments);
            if (string.IsNullOrEmpty(handle))
                return """{"error":"handle is required"}""";

            var record = await tracker.GetAsync(handle, cancellationToken);
            return record is not null
                ? JsonSerializer.Serialize(record)
                : $$$"""{"error":"no run found for handle '{{{handle}}}'"} """;
        }
    }

    // ── cancel_background_agent ──────────────────────────────────────────────

    private sealed class CancelTool(IBackgroundAgentTracker tracker) : ITool
    {
        public string Name => "cancel_background_agent";
        public string Description => "Request cancellation of a running background sub-agent run.";
        public JsonElement ParametersSchema => s_handleSchema;

        public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default)
        {
            var handle = ExtractHandle(arguments);
            if (string.IsNullOrEmpty(handle))
                return """{"error":"handle is required"}""";

            var cancelled = await tracker.CancelAsync(handle, cancellationToken);
            return $$$"""{"handle":"{{{handle}}}","cancelled":{{{(cancelled ? "true" : "false")}}}}""";
        }
    }

    private static string ExtractHandle(JsonElement arguments)
    {
        if (arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty("handle", out var h) &&
            h.ValueKind == JsonValueKind.String)
        {
            return h.GetString() ?? string.Empty;
        }
        return string.Empty;
    }
}
