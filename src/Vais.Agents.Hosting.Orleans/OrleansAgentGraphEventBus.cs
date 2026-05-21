// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Orleans.Streams;

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Orleans-streams-backed <see cref="IAgentGraphEventBus"/> — the graph-scoped sibling of
/// <see cref="OrleansAgentEventBus"/>. Publishes every <see cref="AgentGraphEvent"/> to a single
/// conventional stream; subscribers receive the fan-out through whatever stream provider the host
/// has configured.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stream identity.</b> A single graph-event stream per cluster, addressed as
/// <c>(<see cref="StreamNamespace"/>, <see cref="StreamKey"/>)</c>. Reuses the same registered
/// stream provider as <see cref="OrleansAgentEventBus"/> (provider name
/// <see cref="OrleansAgentEventBus.StreamNamespace"/>) but a distinct stream namespace so graph
/// events stay off the per-turn <see cref="AgentEvent"/> stream (different element type).
/// </para>
/// <para>
/// <b>Cross-silo reach.</b> Like <see cref="OrleansAgentEventBus"/>, the bus is provider-neutral:
/// it fans out across silos only when the host wires a cross-silo provider (Redis via
/// <c>UseAgenticRedisStreaming</c>, or <c>AddEventHubStreams</c>). With the default
/// <c>AddMemoryStreams</c> provider it is same-silo. ADR 019.
/// </para>
/// <para>
/// <b>Thread-safety.</b> Publish and subscribe are safe from multiple threads concurrently.
/// Each returned <see cref="IDisposable"/> owns a single
/// <see cref="StreamSubscriptionHandle{T}"/> and unsubscribes at most once.
/// </para>
/// </remarks>
public sealed class OrleansAgentGraphEventBus : IAgentGraphEventBus
{
    /// <summary>
    /// Conventional stream namespace for graph events. Distinct from
    /// <see cref="OrleansAgentEventBus.StreamNamespace"/> so the two buses share a provider
    /// without sharing a stream.
    /// </summary>
    public const string StreamNamespace = "vais.agents.graph.events";

    /// <summary>Conventional stream id — <see cref="Guid.Empty"/> means "the one global graph-event stream".</summary>
    public static readonly Guid StreamKey = Guid.Empty;

    private readonly IClusterClient _clusterClient;
    private readonly string _streamProviderName;

    /// <summary>
    /// Create a bus bound to a cluster client and the name of an already-registered Orleans
    /// stream provider.
    /// </summary>
    /// <param name="clusterClient">The Orleans cluster client (or silo-side facade).</param>
    /// <param name="streamProviderName">
    /// Name the host passed to <c>siloBuilder.AddMemoryStreams(name)</c> / equivalent. Defaults
    /// to <see cref="OrleansAgentEventBus.StreamNamespace"/> — the provider both buses share.
    /// </param>
    public OrleansAgentGraphEventBus(IClusterClient clusterClient, string streamProviderName)
    {
        ArgumentNullException.ThrowIfNull(clusterClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(streamProviderName);
        _clusterClient = clusterClient;
        _streamProviderName = streamProviderName;
    }

    /// <inheritdoc />
    public async ValueTask PublishAsync(AgentGraphEvent @event, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(@event);

        var stream = GetStream();
        await stream.OnNextAsync(@event).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IDisposable Subscribe(Func<AgentGraphEvent, CancellationToken, ValueTask> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        // Block on the subscribe — the contract returns a synchronous IDisposable, and Orleans
        // stream providers expose a Task-returning SubscribeAsync. Subscribe is a startup-time
        // (not per-event) operation, so blocking here is acceptable, matching OrleansAgentEventBus.
        var stream = GetStream();
        var observer = new ObserverAdapter(handler);
        var handle = stream.SubscribeAsync(observer).GetAwaiter().GetResult();
        return new Subscription(handle);
    }

    private IAsyncStream<AgentGraphEvent> GetStream()
    {
        var provider = _clusterClient.GetStreamProvider(_streamProviderName);
        var streamId = StreamId.Create(StreamNamespace, StreamKey);
        return provider.GetStream<AgentGraphEvent>(streamId);
    }

    private sealed class ObserverAdapter : IAsyncObserver<AgentGraphEvent>
    {
        private readonly Func<AgentGraphEvent, CancellationToken, ValueTask> _handler;

        public ObserverAdapter(Func<AgentGraphEvent, CancellationToken, ValueTask> handler)
        {
            _handler = handler;
        }

        public Task OnNextAsync(AgentGraphEvent item, StreamSequenceToken? token = null)
            => _handler(item, CancellationToken.None).AsTask();

        public Task OnCompletedAsync() => Task.CompletedTask;

        public Task OnErrorAsync(Exception ex) => Task.CompletedTask;
    }

    private sealed class Subscription : IDisposable
    {
        private StreamSubscriptionHandle<AgentGraphEvent>? _handle;

        public Subscription(StreamSubscriptionHandle<AgentGraphEvent> handle)
        {
            _handle = handle;
        }

        public void Dispose()
        {
            var handle = Interlocked.Exchange(ref _handle, null);
            if (handle is not null)
            {
                // Fire-and-forget unsubscribe — best-effort cleanup the runtime may delay/batch.
                _ = handle.UnsubscribeAsync();
            }
        }
    }
}
