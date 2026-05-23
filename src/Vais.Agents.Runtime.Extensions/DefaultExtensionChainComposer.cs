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
/// <remarks>
/// Chains are built by a single seam-agnostic loop (<see cref="BuildChain{TSeam}"/>); each
/// seam contributes only a decorator factory. Adding a seam = one cache entry + one accessor.
/// </remarks>
internal sealed class DefaultExtensionChainComposer : IExtensionChainComposer
{
    private readonly ExtensionHandlerRegistry _registry;
    private readonly IAgentRegistry? _agentRegistry;

    // Per-agentId caches. Null value = pending resolution.
    private readonly ConcurrentDictionary<string, CachedChains> _cache = new(StringComparer.Ordinal);

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
        var chains = await GetOrBuildAsync(agentId, cancellationToken).ConfigureAwait(false);
        return chains.Get<AgentInputMiddleware>(ExtensionSeams.AgentInput);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentOutputMiddleware>> GetOutputChainAsync(
        string agentId,
        CancellationToken cancellationToken = default)
    {
        var chains = await GetOrBuildAsync(agentId, cancellationToken).ConfigureAwait(false);
        return chains.Get<AgentOutputMiddleware>(ExtensionSeams.AgentOutput);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ToolGatewayMiddleware>> GetToolChainAsync(
        string agentId,
        CancellationToken cancellationToken = default)
    {
        var chains = await GetOrBuildAsync(agentId, cancellationToken).ConfigureAwait(false);
        return chains.Get<ToolGatewayMiddleware>(ExtensionSeams.ToolGatewayMiddleware);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LlmGatewayMiddleware>> GetLlmChainAsync(
        string agentId,
        CancellationToken cancellationToken = default)
    {
        var chains = await GetOrBuildAsync(agentId, cancellationToken).ConfigureAwait(false);
        return chains.Get<LlmGatewayMiddleware>(ExtensionSeams.LlmGatewayMiddleware);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ErrorInterceptor>> GetErrorInterceptorChainAsync(
        string agentId,
        CancellationToken cancellationToken = default)
    {
        var chains = await GetOrBuildAsync(agentId, cancellationToken).ConfigureAwait(false);
        return chains.Get<ErrorInterceptor>(ExtensionSeams.ErrorInterceptor);
    }

    /// <inheritdoc />
    public void InvalidateAgent(string agentId) => _cache.TryRemove(agentId, out _);

    /// <inheritdoc />
    public void InvalidateAll() => _cache.Clear();

    private async Task<CachedChains> GetOrBuildAsync(string agentId, CancellationToken cancellationToken)
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

        // One entry per seam. Each seam supplies a decorator factory; the build loop is shared.
        var chains = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [ExtensionSeams.AgentInput] = BuildChain<AgentInputMiddleware>(
                snapshot, manifest, agentId, ExtensionSeams.AgentInput,
                (inst, desc, fm) => inst is AgentInputMiddleware mw
                    ? new InstrumentedInputMiddleware(mw, desc, fm) : null),
            [ExtensionSeams.AgentOutput] = BuildChain<AgentOutputMiddleware>(
                snapshot, manifest, agentId, ExtensionSeams.AgentOutput,
                (inst, desc, fm) => inst is AgentOutputMiddleware mw
                    ? new InstrumentedOutputMiddleware(mw, desc, fm) : null),
            [ExtensionSeams.ToolGatewayMiddleware] = BuildChain<ToolGatewayMiddleware>(
                snapshot, manifest, agentId, ExtensionSeams.ToolGatewayMiddleware,
                (inst, desc, fm) => inst is ToolGatewayMiddleware mw
                    ? new InstrumentedToolMiddleware(mw, desc, fm) : null),
            [ExtensionSeams.LlmGatewayMiddleware] = BuildChain<LlmGatewayMiddleware>(
                snapshot, manifest, agentId, ExtensionSeams.LlmGatewayMiddleware,
                (inst, desc, fm) => inst is LlmGatewayMiddleware mw
                    ? new InstrumentedLlmMiddleware(mw, desc, fm, agentId) : null),
            [ExtensionSeams.ErrorInterceptor] = BuildChain<ErrorInterceptor>(
                snapshot, manifest, agentId, ExtensionSeams.ErrorInterceptor,
                (inst, desc, fm) => inst is ErrorInterceptor ei
                    ? new InstrumentedErrorInterceptor(ei, desc, fm, agentId) : null),
        };

        var built = new CachedChains(chains);
        _cache.TryAdd(agentId, built);
        return _cache.TryGetValue(agentId, out var winner) ? winner : built;
    }

    /// <summary>
    /// Seam-agnostic chain builder: filters the registry snapshot by scope, selects bindings on
    /// <paramref name="seamName"/>, decorates each via <paramref name="decorate"/> (which returns
    /// null when the bound instance does not match the seam type — that binding is skipped), and
    /// returns them sorted by ascending priority.
    /// </summary>
    private IReadOnlyList<TSeam> BuildChain<TSeam>(
        IReadOnlyDictionary<string, ExtensionDescriptor> snapshot,
        AgentManifest? manifest,
        string agentId,
        string seamName,
        Func<object, HandlerBindingDescriptor, string, TSeam?> decorate)
        where TSeam : class
    {
        var items = new List<(int Priority, TSeam Middleware)>();

        foreach (var descriptor in snapshot.Values)
        {
            if (!ExtensionScopeMatcher.Matches(descriptor.Manifest.Spec.Scope, manifest, agentId))
            {
                continue;
            }

            foreach (var binding in descriptor.Handlers)
            {
                if (!string.Equals(binding.Seam, seamName, StringComparison.Ordinal))
                {
                    continue;
                }

                var bindingDescriptor = new HandlerBindingDescriptor(
                    descriptor.ExtensionId, descriptor.Version,
                    binding.HandlerId, binding.Seam, "csharp");

                var decorated = decorate(binding.HandlerInstance, bindingDescriptor, binding.FailureMode);
                if (decorated is not null)
                {
                    items.Add((binding.Priority, decorated));
                }
            }
        }

        items.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        return items.Select(x => x.Middleware).ToArray();
    }

    /// <summary>Per-agent composed chains, keyed by seam name. Each value is a typed <c>TSeam[]</c>.</summary>
    private sealed class CachedChains
    {
        private readonly IReadOnlyDictionary<string, object> _chains;

        public CachedChains(IReadOnlyDictionary<string, object> chains) => _chains = chains;

        public IReadOnlyList<TSeam> Get<TSeam>(string seamName)
            => _chains.TryGetValue(seamName, out var list)
                ? (IReadOnlyList<TSeam>)list
                : Array.Empty<TSeam>();
    }

    // ── Instrumented decorators ───────────────────────────────────────────────

    /// <summary>
    /// Runs <paramref name="invokeInner"/> (a void-returning seam handler), honouring
    /// <paramref name="failureMode"/>: <c>skip</c> swallows a handler exception and ensures the chain
    /// continues, returning <see cref="HandlerOutcome.Skip"/>; otherwise the exception propagates.
    /// The returned outcome distinguishes pass-through (<c>next</c> called) from short-circuit.
    /// </summary>
    private static async Task<HandlerOutcome> RunWithFailureModeAsync(
        Func<Func<Task>, Task> invokeInner,
        Func<Task> next,
        string failureMode,
        CancellationToken ct)
    {
        var nextCalled = false;
        Task TrackingNext() { nextCalled = true; return next(); }

        if (string.Equals(failureMode, "skip", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await invokeInner(TrackingNext).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                if (!nextCalled) await next().ConfigureAwait(false);
                return HandlerOutcome.Skip(ex);
            }
        }
        else
        {
            await invokeInner(TrackingNext).ConfigureAwait(false);
        }

        return nextCalled ? HandlerOutcome.Next() : HandlerOutcome.ShortCircuit();
    }

    /// <summary>
    /// Value-returning analogue of <see cref="RunWithFailureModeAsync"/> for seams whose handler
    /// returns a result (tool/llm gateway). Returns both the produced result and the
    /// <see cref="HandlerOutcome"/> (next when the handler delegated, shortCircuit when it produced
    /// its own result without calling next). On <c>failureMode=skip</c> a handler exception is
    /// swallowed and the underlying result is used (calling <c>next</c> if the handler had not).
    /// </summary>
    private static async Task<(T Result, HandlerOutcome Handler)> RunValueWithFailureModeAsync<T>(
        Func<Func<Task<T>>, Task<T>> invokeInner,
        Func<Task<T>> next,
        string failureMode,
        CancellationToken ct)
    {
        var nextCalled = false;
        T nextResult = default!;
        async Task<T> TrackingNext()
        {
            nextCalled = true;
            nextResult = await next().ConfigureAwait(false);
            return nextResult;
        }

        if (string.Equals(failureMode, "skip", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var result = await invokeInner(TrackingNext).ConfigureAwait(false);
                return (result, nextCalled ? HandlerOutcome.Next() : HandlerOutcome.ShortCircuit());
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                var fallback = nextCalled ? nextResult : await next().ConfigureAwait(false);
                return (fallback, HandlerOutcome.Skip(ex));
            }
        }

        var ok = await invokeInner(TrackingNext).ConfigureAwait(false);
        return (ok, nextCalled ? HandlerOutcome.Next() : HandlerOutcome.ShortCircuit());
    }

    private sealed class InstrumentedToolMiddleware(
        ToolGatewayMiddleware inner,
        HandlerBindingDescriptor descriptor,
        string failureMode) : ToolGatewayMiddleware
    {
        public override async Task<ToolCallOutcome> InvokeAsync(
            ToolGatewayContext ctx, Func<Task<ToolCallOutcome>> next, CancellationToken ct = default)
        {
            ToolCallOutcome? captured = null;
            await ExtensionInvocationInstrumentation.InvokeWithInstrumentationAsync(
                descriptor, ctx.AgentContext.AgentName ?? "", ctx.AgentContext.RunId, nodeId: null,
                async () =>
                {
                    var (outcome, handler) = await RunValueWithFailureModeAsync(
                        tn => inner.InvokeAsync(ctx, tn, ct), next, failureMode, ct).ConfigureAwait(false);
                    captured = outcome;
                    return handler;
                },
                ct).ConfigureAwait(false);
            return captured!;
        }
    }

    private sealed class InstrumentedLlmMiddleware(
        LlmGatewayMiddleware inner,
        HandlerBindingDescriptor descriptor,
        string failureMode,
        string agentId) : LlmGatewayMiddleware
    {
        // Non-streaming path: instrumented with the per-call span (capture pattern). runId is not
        // available here (CompletionRequest carries no run identity), so the span is tagged with the
        // build-time agentId only.
        protected override async Task<CompletionResponse> InvokeAsync(
            CompletionRequest request,
            Func<CompletionRequest, CancellationToken, Task<CompletionResponse>> next,
            CancellationToken cancellationToken)
        {
            CompletionResponse? captured = null;
            await ExtensionInvocationInstrumentation.InvokeWithInstrumentationAsync(
                descriptor, agentId, runId: null, nodeId: null,
                async () =>
                {
                    var nextCalled = false;
                    CompletionResponse nextResult = default!;
                    async Task<CompletionResponse> TrackingNext(CompletionRequest req, CancellationToken ct)
                    {
                        nextCalled = true;
                        nextResult = await next(req, ct).ConfigureAwait(false);
                        return nextResult;
                    }

                    if (string.Equals(failureMode, "skip", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            captured = await ((IAgentFilter)inner).InvokeAsync(request, TrackingNext, cancellationToken).ConfigureAwait(false);
                            return nextCalled ? HandlerOutcome.Next() : HandlerOutcome.ShortCircuit();
                        }
                        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                        {
                            captured = nextCalled ? nextResult : await next(request, cancellationToken).ConfigureAwait(false);
                            return HandlerOutcome.Skip(ex);
                        }
                    }

                    captured = await ((IAgentFilter)inner).InvokeAsync(request, TrackingNext, cancellationToken).ConfigureAwait(false);
                    return nextCalled ? HandlerOutcome.Next() : HandlerOutcome.ShortCircuit();
                },
                cancellationToken).ConfigureAwait(false);
            return captured!;
        }

        // Streaming + observation hooks: delegate to the inner handler to preserve its behavior. The
        // per-call span covers the non-streaming path; the Task-based instrumentation helper does not
        // model an enumerable's lifetime, so streaming extension middleware runs uninstrumented.
        protected override IAsyncEnumerable<CompletionUpdate> InvokeStreamAsync(
            CompletionRequest request,
            Func<CompletionRequest, CancellationToken, IAsyncEnumerable<CompletionUpdate>> next,
            CancellationToken cancellationToken)
            => ((IStreamingAgentFilter)inner).InvokeAsync(request, next, cancellationToken);

        protected override ValueTask<CompletionUpdate> OnDeltaAsync(
            CompletionUpdate update, CancellationToken cancellationToken = default)
            => ((IStreamingAgentFilter)inner).OnStreamDeltaAsync(update, cancellationToken);

        protected override ValueTask OnStreamCompleteAsync(
            CompletionResponse final, CancellationToken cancellationToken = default)
            => ((IStreamingAgentFilter)inner).OnStreamCompleteAsync(final, cancellationToken);
    }

    private sealed class InstrumentedErrorInterceptor(
        ErrorInterceptor inner,
        HandlerBindingDescriptor descriptor,
        string failureMode,
        string agentId) : ErrorInterceptor
    {
        public override async Task<ErrorOutcome> OnErrorAsync(ErrorContext ctx, CancellationToken ct = default)
        {
            var outcome = ErrorOutcome.Observe;
            await ExtensionInvocationInstrumentation.InvokeWithInstrumentationAsync(
                descriptor,
                string.IsNullOrEmpty(ctx.AgentId) ? agentId : ctx.AgentId,
                ctx.RunId, ctx.NodeId,
                async () =>
                {
                    if (string.Equals(failureMode, "skip", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            outcome = await inner.OnErrorAsync(ctx, ct).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (!ct.IsCancellationRequested)
                        {
                            return HandlerOutcome.Skip(ex);
                        }
                    }
                    else
                    {
                        outcome = await inner.OnErrorAsync(ctx, ct).ConfigureAwait(false);
                    }

                    return string.IsNullOrEmpty(outcome.Message) ? HandlerOutcome.Next() : HandlerOutcome.Mutate();
                },
                ct).ConfigureAwait(false);
            return outcome;
        }
    }

    private sealed class InstrumentedInputMiddleware(
        AgentInputMiddleware inner,
        HandlerBindingDescriptor descriptor,
        string failureMode) : AgentInputMiddleware
    {
        public override Task InvokeAsync(AgentInputContext ctx, Func<Task> next, CancellationToken ct = default)
            => ExtensionInvocationInstrumentation.InvokeWithInstrumentationAsync(
                descriptor, ctx.AgentId, ctx.RunId, ctx.NodeId,
                () => RunWithFailureModeAsync(tn => inner.InvokeAsync(ctx, tn, ct), next, failureMode, ct),
                ct);
    }

    private sealed class InstrumentedOutputMiddleware(
        AgentOutputMiddleware inner,
        HandlerBindingDescriptor descriptor,
        string failureMode) : AgentOutputMiddleware
    {
        public override Task InvokeAsync(AgentOutputContext ctx, Func<Task> next, CancellationToken ct = default)
            => ExtensionInvocationInstrumentation.InvokeWithInstrumentationAsync(
                descriptor, ctx.AgentId, ctx.RunId, nodeId: null,
                () => RunWithFailureModeAsync(tn => inner.InvokeAsync(ctx, tn, ct), next, failureMode, ct),
                ct);
    }
}
