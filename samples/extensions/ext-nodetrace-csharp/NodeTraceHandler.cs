// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

[assembly: Vais.Agents.VaisExtension(
    TargetApiVersion = "0.30",
    Handlers = new[] { typeof(Vais.Agents.Samples.Extensions.NodeTrace.NodeTracer) })]

namespace Vais.Agents.Samples.Extensions.NodeTrace;

/// <summary>
/// Sample <see cref="GraphNodeMiddleware"/> extension: traces per-node timing and demonstrates a
/// node-level cache short-circuit. Demonstrates <c>host: csharp</c> extension-authored graph node
/// governance — the same wrap a co-tenant container agent gets via <c>host: container</c>
/// (see ext-nodetrace-python, mirrored shape).
/// </summary>
/// <remarks>
/// When a node's input carries <c>cacheHit: true</c>, the handler returns a canned output WITHOUT
/// running the node body (the short-circuit / cache path). Otherwise it times the body and logs the
/// duration. Short-circuit is journaling-safe: the substitute output merges + checkpoints exactly
/// like a real run, so resume stays consistent.
/// </remarks>
public sealed class NodeTracer : GraphNodeMiddleware
{
    private readonly ILogger<NodeTracer> _log;

    public NodeTracer(ILogger<NodeTracer> log) => _log = log;

    public override async Task<GraphNodeOutcome> InvokeAsync(
        GraphNodeContext context, Func<Task<GraphNodeOutcome>> next, CancellationToken cancellationToken = default)
    {
        if (context.Input.TryGetValue("cacheHit", out var hit) && hit.ValueKind == JsonValueKind.True)
        {
            _log.LogInformation("[ext-nodetrace] cache hit node={Node} agent={Agent} — short-circuiting body",
                context.NodeId, context.AgentId);
            return new GraphNodeOutcome(new Dictionary<string, JsonElement>
            {
                ["lastAssistantText"] = JsonSerializer.SerializeToElement("(cached)"),
            });
        }

        var sw = Stopwatch.StartNew();
        _log.LogInformation("[ext-nodetrace] node start node={Node} kind={Kind} agent={Agent} step={Step}",
            context.NodeId, context.NodeKind, context.AgentId, context.SuperStep);

        var outcome = await next().ConfigureAwait(false);

        _log.LogInformation("[ext-nodetrace] node done node={Node} elapsedMs={Ms} outputKeys=[{Keys}]",
            context.NodeId, sw.ElapsedMilliseconds, string.Join(",", outcome.Output.Keys));
        return outcome;
    }
}
