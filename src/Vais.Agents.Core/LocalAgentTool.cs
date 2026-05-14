// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Vais.Agents.Core;

/// <summary>
/// <see cref="ITool"/> adapter that invokes a local (same-runtime) agent as a
/// blocking sub-task so a coordinator agent can delegate work in-process without
/// an HTTP round-trip. Implements the P7 principle ("agent-as-tool over peer A2A
/// is the default").
/// </summary>
/// <remarks>
/// <para>
/// <b>Session isolation.</b> Each call derives a deterministic child session id
/// from the parent run id + tool name + argument hash. The child session is evicted
/// after the call unless the caller opted into <c>allowCallerSuppliedSession</c>
/// (multi-turn sub-conversations). Caller-supplied sessions persist across calls.
/// </para>
/// <para>
/// <b>Depth guard.</b> <see cref="AgentContext.MaxChainDepth"/> is decremented for
/// the child context. When the depth reaches zero the call returns a structured
/// error string rather than throwing, keeping it a tool-call failure rather than
/// a turn abort.
/// </para>
/// <para>
/// <b>Context descent.</b> The child inherits
/// <see cref="AgentContext.UserId"/>, <see cref="AgentContext.TenantId"/>,
/// <see cref="AgentContext.WorkspaceId"/>, <see cref="AgentContext.CorrelationId"/>,
/// <see cref="AgentContext.PrivilegeLevel"/>, <see cref="AgentContext.AutonomyLevel"/>
/// from the caller. <see cref="AgentContext.AllowedTools"/> is propagated or
/// cleared based on <see cref="LocalAgentRef.PropagateAllowedTools"/>.
/// <see cref="AgentContext.RunId"/> is set to the child session id for per-child
/// journal scoping.
/// </para>
/// </remarks>
public sealed class LocalAgentTool : ITool
{
    private static readonly JsonElement s_defaultSchema = JsonDocument.Parse(
        """{"type":"object","properties":{"message":{"type":"string","description":"Text prompt to send to the sub-agent."}},"required":["message"]}""")
        .RootElement.Clone();

    private static readonly JsonElement s_sessionSchema = JsonDocument.Parse(
        """{"type":"object","properties":{"message":{"type":"string","description":"Text prompt to send to the sub-agent."},"sessionId":{"type":"string","description":"Optional session identifier for multi-turn sub-conversations."}},"required":["message"]}""")
        .RootElement.Clone();

    private readonly Func<IAgentRuntime> _runtimeFactory;
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
    /// Construct a <see cref="LocalAgentTool"/>.
    /// </summary>
    /// <param name="runtimeFactory">
    /// Lazy factory for <see cref="IAgentRuntime"/>. Resolved at first invoke to
    /// avoid a DI cycle (the runtime uses the translator, which creates this tool).
    /// </param>
    /// <param name="effectiveAgentId">Resolved agent id in the registry (AgentId ?? Name).</param>
    /// <param name="name">Tool name exposed to the LLM (must match <c>[A-Za-z0-9_-]+</c>).</param>
    /// <param name="description">Tool description exposed to the LLM.</param>
    /// <param name="allowCallerSuppliedSession">Extend schema with optional <c>sessionId</c> argument.</param>
    /// <param name="propagateAllowedTools">Propagate caller's <see cref="AgentContext.AllowedTools"/> to child.</param>
    public LocalAgentTool(
        Func<IAgentRuntime> runtimeFactory,
        string effectiveAgentId,
        string name,
        string description,
        bool allowCallerSuppliedSession = false,
        bool propagateAllowedTools = true)
    {
        ArgumentNullException.ThrowIfNull(runtimeFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(effectiveAgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        _runtimeFactory = runtimeFactory;
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

        // Read ambient caller context. _current in AsyncLocalAgentContextAccessor is static, so any
        // instance reads the same AsyncLocal slot — no DI lookup needed.
        var callerCtx = new AsyncLocalAgentContextAccessor().Current;

        // Depth guard: reject before creating a child session.
        if (callerCtx.MaxChainDepth is <= 0)
        {
            return $"[LocalAgentTool:{Name}] Chain depth limit reached; sub-agent call rejected.";
        }

        // Derive deterministic child session id, or use caller-supplied.
        bool callerSupplied = false;
        string childSessionId;
        if (_allowCallerSuppliedSession &&
            arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty("sessionId", out var sidEl) &&
            sidEl.ValueKind == JsonValueKind.String &&
            sidEl.GetString() is { Length: > 0 } suppliedSid)
        {
            // Namespace under parent run to prevent cross-run key collisions.
            var parentRun = callerCtx.RunId ?? "no-run";
            childSessionId = SanitiseSessionId($"{parentRun}:{suppliedSid}");
            callerSupplied = true;
        }
        else
        {
            var parentRun = callerCtx.RunId;
            childSessionId = parentRun is not null
                ? $"{SanitiseSessionId(parentRun)}__{SanitiseSessionId(Name)}__{ArgHash(arguments)}"
                : $"localcall-{Guid.NewGuid():N}";
        }

        // Build child context: inherit identity + RCB fields, decrement depth, scope RunId.
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

        var runtime = _runtimeFactory();
        var child = runtime.GetOrCreateForSession(_effectiveAgentId, childSessionId);

        // Push child context for the duration of the call.
        using var _ = new AsyncLocalAgentContextAccessor().Push(childCtx);
        try
        {
            return await child.AskAsync(message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Deterministic sessions are ephemeral; caller-supplied sessions persist.
            if (!callerSupplied)
            {
                runtime.RemoveSession(_effectiveAgentId, childSessionId);
            }
        }
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

    /// <summary>
    /// Replace characters forbidden in Orleans grain key segments (the '/' separator)
    /// and collapse runs of the replacement character '_'.
    /// </summary>
    internal static string SanitiseSessionId(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        var sb = new StringBuilder(raw.Length);
        var lastWasUnderscore = false;
        foreach (var ch in raw)
        {
            if (ch == '/')
            {
                if (!lastWasUnderscore) { sb.Append('_'); lastWasUnderscore = true; }
            }
            else
            {
                sb.Append(ch);
                lastWasUnderscore = ch == '_';
            }
        }
        return sb.ToString();
    }

    private static string ArgHash(JsonElement arguments)
    {
        var raw = arguments.ValueKind == JsonValueKind.Undefined
            ? "empty"
            : arguments.GetRawText();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexStringLower(bytes[..8]);
    }
}
