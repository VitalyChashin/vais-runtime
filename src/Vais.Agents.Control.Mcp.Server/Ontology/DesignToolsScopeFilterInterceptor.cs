// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using ModelContextProtocol.Protocol;

namespace Vais.Agents.Control.Mcp.Server.Ontology;

/// <summary>
/// Substrate-shaped re-expression of the Plan B NB-12 scope filter: when the caller is
/// authorized to mutate at least one resource kind, append the mutating verbs
/// (<c>vais.apply</c>, <c>vais.delete</c>, <c>vais.eval</c>, <c>vais.eval.status</c>) to the
/// read-only baseline. Otherwise pass the baseline through unchanged. Byte-identical to the
/// previously inlined <c>CanMutateAnythingAsync</c> path.
/// </summary>
/// <remarks>
/// Declares <see cref="InterceptorKind.Mutation"/> because the response phase rewrites the
/// tool set — the interceptor itself performs no state mutation against any registry. The
/// chain runs read-side; the policy probes are <c>Create</c>-shaped only to test which kinds
/// the caller could theoretically author.
/// </remarks>
internal sealed class DesignToolsScopeFilterInterceptor(
    IAgentPolicyEngine? policy = null,
    IAgentContextAccessor? accessor = null)
    : OntologyInterceptor<DesignToolsListInterceptionContext, ListToolsResult>
{
    private static readonly PolicyOperation[] MutationProbes =
    [
        PolicyOperation.Create, PolicyOperation.GraphCreate, PolicyOperation.McpServerCreate,
        PolicyOperation.McpGatewayConfigCreate, PolicyOperation.LlmGatewayConfigCreate, PolicyOperation.ContainerPluginCreate,
        PolicyOperation.EvalSuiteUpsert,
    ];

    private readonly IAgentPolicyEngine _policy = policy ?? NullAgentPolicyEngine.Instance;
    private readonly IAgentContextAccessor? _accessor = accessor;

    public override InterceptorKind Kind => InterceptorKind.Mutation;

    public override async Task<ListToolsResult> InvokeAsync(
        DesignToolsListInterceptionContext context,
        Func<Task<ListToolsResult>> next,
        CancellationToken cancellationToken = default)
    {
        var baseline = await next().ConfigureAwait(false);
        if (!await CanMutateAnythingAsync(cancellationToken).ConfigureAwait(false))
            return baseline;
        var combined = new List<Tool>(baseline.Tools.Count + DesignMcpToolHandlers.MutatingTools.Count);
        combined.AddRange(baseline.Tools);
        combined.AddRange(DesignMcpToolHandlers.MutatingTools);
        return new ListToolsResult { Tools = combined };
    }

    private async ValueTask<bool> CanMutateAnythingAsync(CancellationToken ct)
    {
        var principal = BuildPrincipal();
        foreach (var op in MutationProbes)
        {
            var decision = await _policy.EvaluateAsync(op, manifest: null, principal, ct).ConfigureAwait(false);
            if (decision.IsAllowed) return true;
        }
        return false;
    }

    private AgentPrincipal? BuildPrincipal()
    {
        var ctx = _accessor?.Current ?? AgentContext.Empty;
        return ctx.UserId is { Length: > 0 } userId
            ? new AgentPrincipal(userId, ctx.TenantId, ctx.Scopes)
            : null;
    }
}
