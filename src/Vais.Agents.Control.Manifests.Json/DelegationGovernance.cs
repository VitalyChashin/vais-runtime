// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json.Nodes;

namespace Vais.Agents.Control.Manifests;

/// <summary>
/// Decision returned by an <see cref="IDelegationPolicy"/> for one agent-as-tool call. When
/// <see cref="Allowed"/> is <see langword="false"/> the middleware short-circuits with a
/// structured failure outcome — same shape as the south cartridge's arg-validation refusal
/// (<c>{ok:false, reason, suggestions[]}</c>) — so the LLM can adapt without a turn abort.
/// </summary>
/// <param name="Allowed">True ⇒ pass through to upstream. False ⇒ short-circuit with the supplied reason.</param>
/// <param name="Reason">Human-readable explanation surfaced to the model when denied. Required when <see cref="Allowed"/> is false.</param>
/// <param name="Suggestions">Optional follow-up hints the model can read to recover.</param>
public sealed record DelegationDecision(
    bool Allowed,
    string? Reason = null,
    IReadOnlyList<string>? Suggestions = null)
{
    /// <summary>Always-allowed decision (singleton, since allowed decisions carry no payload).</summary>
    public static DelegationDecision Allow { get; } = new(true, null, null);

    /// <summary>Deny with a reason and optional suggestions.</summary>
    public static DelegationDecision Deny(string reason, IReadOnlyList<string>? suggestions = null) =>
        new(false, reason, suggestions);
}

/// <summary>
/// Deployer-supplied policy that decides whether a single agent-as-tool delegation may
/// proceed. Inputs: the gateway context for this call plus the bound <see cref="CapabilityMap"/>
/// (so the policy can reason about which sub-agent is being called, its tags, etc.). The
/// policy is responsible for any per-run history tracking (preconditions, cost counters,
/// cycle detection beyond the existing depth guard).
/// </summary>
public interface IDelegationPolicy
{
    /// <summary>Decide whether <paramref name="context"/> may invoke its sub-agent tool.</summary>
    ValueTask<DelegationDecision> EvaluateAsync(
        ToolGatewayContext context,
        CapabilityMap map,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Substrate-shaped <see cref="ToolGatewayMiddleware"/> (<see cref="InterceptorKind.Validation"/>,
/// request-phase) that runs <see cref="IDelegationPolicy"/> against every tool call whose name
/// matches a sub-agent in the coordinator's <see cref="CapabilityMap"/>. Regular tools
/// (non-agent-as-tool calls) pass through unchanged.
/// </summary>
/// <remarks>
/// Plan C2-5: layered on top of the existing <c>LocalAgentTool</c> depth guard, never
/// replacing it. Returns a structured <c>{ok:false, reason, suggestions[]}</c> tool-call
/// outcome on deny — the LLM sees a normal tool-call failure and can adapt; this is *not* a
/// turn abort. Policy content is deployment-local; the OSS default is the always-allow
/// <see cref="DelegationDecision.Allow"/> path (see <see cref="AllowAllDelegationPolicy"/>).
/// </remarks>
public sealed class DelegationGovernanceMiddleware(
    IDelegationPolicy policy,
    IAgentCapabilityMapBuilder mapBuilder) : ToolGatewayMiddleware
{
    private readonly IDelegationPolicy _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    private readonly IAgentCapabilityMapBuilder _mapBuilder = mapBuilder ?? throw new ArgumentNullException(nameof(mapBuilder));

    /// <inheritdoc />
    public override InterceptorKind Kind => InterceptorKind.Validation;

    /// <inheritdoc />
    public override async Task<ToolCallOutcome> InvokeAsync(
        ToolGatewayContext context,
        Func<Task<ToolCallOutcome>> next,
        CancellationToken cancellationToken = default)
    {
        // Need a coordinator identity to look up the map. When the host populates
        // AgentContext.AgentName we can resolve; otherwise pass through (the middleware is a
        // no-op for headless / unidentified contexts to keep test + library scenarios clean).
        var coordinatorId = context.AgentContext.AgentName;
        if (string.IsNullOrEmpty(coordinatorId)) return await next().ConfigureAwait(false);

        var map = await _mapBuilder.BuildAsync(coordinatorId, cancellationToken).ConfigureAwait(false);
        if (!IsSubAgentCall(map, context.ToolName)) return await next().ConfigureAwait(false);

        var decision = await _policy.EvaluateAsync(context, map, cancellationToken).ConfigureAwait(false);
        if (decision.Allowed) return await next().ConfigureAwait(false);

        var suggestions = new JsonArray();
        foreach (var s in decision.Suggestions ?? []) suggestions.Add(s);
        var payload = new JsonObject
        {
            ["ok"] = false,
            ["reason"] = decision.Reason ?? "delegation denied",
            ["suggestions"] = suggestions,
        };
        return new ToolCallOutcome(context.CallId, payload.ToJsonString());
    }

    private static bool IsSubAgentCall(CapabilityMap map, string toolName)
    {
        for (var i = 0; i < map.SubAgents.Count; i++)
            if (string.Equals(map.SubAgents[i].ToolName, toolName, StringComparison.Ordinal)) return true;
        return false;
    }
}

/// <summary>Default <see cref="IDelegationPolicy"/> that allows every delegation. Use as a registration default; deployers override.</summary>
public sealed class AllowAllDelegationPolicy : IDelegationPolicy
{
    /// <summary>Singleton instance — stateless.</summary>
    public static IDelegationPolicy Instance { get; } = new AllowAllDelegationPolicy();

    /// <inheritdoc />
    public ValueTask<DelegationDecision> EvaluateAsync(
        ToolGatewayContext context, CapabilityMap map, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(DelegationDecision.Allow);
}
