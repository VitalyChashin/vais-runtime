// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents.Core;

/// <summary>
/// <see cref="ITool"/> adapter that starts a background (fire-and-forget) local
/// agent sub-run via <see cref="IBackgroundAgentTracker"/>. Returns a JSON handle
/// so the coordinator can poll status via the management tools.
/// </summary>
public sealed class BackgroundLocalAgentTool : ITool
{
    private static readonly JsonElement s_defaultSchema = JsonDocument.Parse(
        """{"type":"object","properties":{"message":{"type":"string","description":"Text prompt to send to the sub-agent."}},"required":["message"]}""")
        .RootElement.Clone();

    private static readonly JsonElement s_sessionSchema = JsonDocument.Parse(
        """{"type":"object","properties":{"message":{"type":"string","description":"Text prompt to send to the sub-agent."},"sessionId":{"type":"string","description":"Optional session identifier for multi-turn sub-conversations."}},"required":["message"]}""")
        .RootElement.Clone();

    private readonly Func<IAgentRuntime> _runtimeFactory;
    private readonly IBackgroundAgentTracker _tracker;
    private readonly string _effectiveAgentId;
    private readonly bool _allowCallerSuppliedSession;
    private readonly bool _propagateAllowedTools;

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public string Description { get; }

    /// <inheritdoc />
    public JsonElement ParametersSchema => _allowCallerSuppliedSession ? s_sessionSchema : s_defaultSchema;

    /// <summary>
    /// Construct a <see cref="BackgroundLocalAgentTool"/>.
    /// </summary>
    public BackgroundLocalAgentTool(
        Func<IAgentRuntime> runtimeFactory,
        IBackgroundAgentTracker tracker,
        string effectiveAgentId,
        string name,
        string description,
        bool allowCallerSuppliedSession = false,
        bool propagateAllowedTools = true)
    {
        ArgumentNullException.ThrowIfNull(runtimeFactory);
        ArgumentNullException.ThrowIfNull(tracker);
        ArgumentException.ThrowIfNullOrWhiteSpace(effectiveAgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        _runtimeFactory = runtimeFactory;
        _tracker = tracker;
        _effectiveAgentId = effectiveAgentId;
        _allowCallerSuppliedSession = allowCallerSuppliedSession;
        _propagateAllowedTools = propagateAllowedTools;
        Name = name;
        Description = description;
    }

    /// <inheritdoc />
    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var message = ExtractMessage(arguments);
        var callerCtx = new AsyncLocalAgentContextAccessor().Current;

        if (callerCtx.MaxChainDepth is <= 0)
        {
            return $"[BackgroundLocalAgentTool:{Name}] Chain depth limit reached; sub-agent call rejected.";
        }

        string childSessionId;
        if (_allowCallerSuppliedSession &&
            arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty("sessionId", out var sidEl) &&
            sidEl.ValueKind == JsonValueKind.String &&
            sidEl.GetString() is { Length: > 0 } suppliedSid)
        {
            var parentRun = callerCtx.RunId ?? "no-run";
            childSessionId = LocalAgentTool.SanitiseSessionId($"{parentRun}:{suppliedSid}");
        }
        else
        {
            var parentRun = callerCtx.RunId;
            childSessionId = parentRun is not null
                ? $"{LocalAgentTool.SanitiseSessionId(parentRun)}__{LocalAgentTool.SanitiseSessionId(Name)}__{ArgHash(arguments)}"
                : $"bgcall-{Guid.NewGuid():N}";
        }

        var childCtx = new AgentContext(
            UserId: callerCtx.UserId,
            TenantId: callerCtx.TenantId,
            CorrelationId: callerCtx.CorrelationId,
            AgentName: _effectiveAgentId)
        {
            WorkspaceId = callerCtx.WorkspaceId,
            PrivilegeLevel = callerCtx.PrivilegeLevel,
            AutonomyLevel = callerCtx.AutonomyLevel,
            AllowedTools = _propagateAllowedTools ? callerCtx.AllowedTools : null,
            MaxChainDepth = callerCtx.MaxChainDepth is { } d ? d - 1 : (int?)null,
            RunId = childSessionId,
        };

        var parentRunId = callerCtx.RunId ?? "no-run";
        var handle = await _tracker.StartAsync(
            parentRunId, _effectiveAgentId, childSessionId, message, childCtx, cancellationToken);

        return $$$"""{"handle":"{{{handle}}}","status":"pending"}""";
    }

    private static string ExtractMessage(JsonElement arguments)
    {
        if (arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty("message", out var msg) &&
            msg.ValueKind == JsonValueKind.String)
        {
            return msg.GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    private static string ArgHash(JsonElement arguments)
    {
        var raw = arguments.ValueKind == JsonValueKind.Undefined ? "empty" : arguments.GetRawText();
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexStringLower(bytes[..8]);
    }
}
