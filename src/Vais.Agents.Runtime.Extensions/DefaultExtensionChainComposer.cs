// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;

namespace Vais.Agents.Runtime.Extensions;

/// <summary>
/// Default implementation of <see cref="IExtensionChainComposer"/>. Caches the composed
/// chains per agent activation; invalidates on extension swap/unload. Scope matching
/// uses <see cref="ExtensionScopeMatcher"/> against the agent manifest from <see cref="IAgentRegistry"/>.
/// When no registry is available, scope matching is skipped (cluster-wide fallback).
/// In-process middleware instances are wrapped in instrumented decorators that emit
/// <c>vais.extension.handler.invoke</c> spans and metrics per invocation.
/// </summary>
internal sealed class DefaultExtensionChainComposer : IExtensionChainComposer
{
    private readonly ExtensionHandlerRegistry _registry;
    private readonly IAgentRegistry? _agentRegistry;

    // Per-agentId caches. Null value = pending resolution.
    private readonly ConcurrentDictionary<string, CachedChain> _cache = new(StringComparer.Ordinal);

    public DefaultExtensionChainComposer(
        ExtensionHandlerRegistry registry,
        IAgentRegistry? agentRegistry = null)
    {
        _registry = registry;
        _agentRegistry = agentRegistry;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentInputMiddleware>> GetInputChainAsync(
        string agentId,
        CancellationToken cancellationToken = default)
    {
        var chain = await GetOrBuildAsync(agentId, cancellationToken).ConfigureAwait(false);
        return chain.Input;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentOutputMiddleware>> GetOutputChainAsync(
        string agentId,
        CancellationToken cancellationToken = default)
    {
        var chain = await GetOrBuildAsync(agentId, cancellationToken).ConfigureAwait(false);
        return chain.Output;
    }

    /// <inheritdoc />
    public void InvalidateAgent(string agentId) => _cache.TryRemove(agentId, out _);

    /// <inheritdoc />
    public void InvalidateAll() => _cache.Clear();

    private async Task<CachedChain> GetOrBuildAsync(string agentId, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(agentId, out var cached))
        {
            return cached;
        }

        AgentManifest? manifest = null;
        if (_agentRegistry is not null)
        {
            manifest = await _agentRegistry.GetAsync(agentId, version: null, cancellationToken).ConfigureAwait(false);
        }

        var snapshot = _registry.Snapshot();
        var inputs = new List<(int Priority, AgentInputMiddleware Middleware)>();
        var outputs = new List<(int Priority, AgentOutputMiddleware Middleware)>();

        foreach (var descriptor in snapshot.Values)
        {
            foreach (var binding in descriptor.Handlers)
            {
                if (!ExtensionScopeMatcher.Matches(descriptor.Manifest.Spec.Scope, manifest, agentId))
                {
                    continue;
                }

                var bindingDescriptor = new HandlerBindingDescriptor(
                    descriptor.ExtensionId, descriptor.Version,
                    binding.HandlerId, binding.Seam, "csharp");

                switch (binding.Seam)
                {
                    case ExtensionSeams.AgentInput when binding.HandlerInstance is AgentInputMiddleware inputMw:
                        inputs.Add((binding.Priority,
                            new InstrumentedInputMiddleware(inputMw, bindingDescriptor, binding.FailureMode)));
                        break;
                    case ExtensionSeams.AgentOutput when binding.HandlerInstance is AgentOutputMiddleware outputMw:
                        outputs.Add((binding.Priority,
                            new InstrumentedOutputMiddleware(outputMw, bindingDescriptor, binding.FailureMode)));
                        break;
                }
            }
        }

        inputs.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        outputs.Sort((a, b) => a.Priority.CompareTo(b.Priority));

        var built = new CachedChain(
            Input: inputs.Select(x => x.Middleware).ToArray(),
            Output: outputs.Select(x => x.Middleware).ToArray());

        _cache.TryAdd(agentId, built);
        return _cache.TryGetValue(agentId, out var winner) ? winner : built;
    }

    private sealed record CachedChain(
        IReadOnlyList<AgentInputMiddleware> Input,
        IReadOnlyList<AgentOutputMiddleware> Output);

    // ── Instrumented decorators ───────────────────────────────────────────────

    private sealed class InstrumentedInputMiddleware(
        AgentInputMiddleware inner,
        HandlerBindingDescriptor descriptor,
        string failureMode) : AgentInputMiddleware
    {
        public override Task InvokeAsync(AgentInputContext ctx, Func<Task> next, CancellationToken ct = default)
            => ExtensionInvocationInstrumentation.InvokeWithInstrumentationAsync(
                descriptor, ctx.AgentId, ctx.RunId, ctx.NodeId,
                () => InvokeCoreAsync(ctx, next, ct),
                ct);

        private async Task<HandlerOutcome> InvokeCoreAsync(
            AgentInputContext ctx, Func<Task> next, CancellationToken ct)
        {
            var nextCalled = false;
            Task TrackingNext() { nextCalled = true; return next(); }

            if (string.Equals(failureMode, "skip", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await inner.InvokeAsync(ctx, TrackingNext, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    if (!nextCalled) await next().ConfigureAwait(false);
                    return HandlerOutcome.Skip(ex);
                }
            }
            else
            {
                await inner.InvokeAsync(ctx, TrackingNext, ct).ConfigureAwait(false);
            }

            return nextCalled ? HandlerOutcome.Next() : HandlerOutcome.ShortCircuit();
        }
    }

    private sealed class InstrumentedOutputMiddleware(
        AgentOutputMiddleware inner,
        HandlerBindingDescriptor descriptor,
        string failureMode) : AgentOutputMiddleware
    {
        public override Task InvokeAsync(AgentOutputContext ctx, Func<Task> next, CancellationToken ct = default)
            => ExtensionInvocationInstrumentation.InvokeWithInstrumentationAsync(
                descriptor, ctx.AgentId, ctx.RunId, nodeId: null,
                () => InvokeCoreAsync(ctx, next, ct),
                ct);

        private async Task<HandlerOutcome> InvokeCoreAsync(
            AgentOutputContext ctx, Func<Task> next, CancellationToken ct)
        {
            var nextCalled = false;
            Task TrackingNext() { nextCalled = true; return next(); }

            if (string.Equals(failureMode, "skip", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    await inner.InvokeAsync(ctx, TrackingNext, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    if (!nextCalled) await next().ConfigureAwait(false);
                    return HandlerOutcome.Skip(ex);
                }
            }
            else
            {
                await inner.InvokeAsync(ctx, TrackingNext, ct).ConfigureAwait(false);
            }

            return nextCalled ? HandlerOutcome.Next() : HandlerOutcome.ShortCircuit();
        }
    }
}
