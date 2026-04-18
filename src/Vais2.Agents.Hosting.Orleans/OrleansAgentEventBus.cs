// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Orleans.Streams;

namespace Vais2.Agents.Hosting.Orleans;

/// <summary>
/// Orleans-streams-backed <see cref="IAgentEventBus"/>. Publishes every event to a
/// single conventional stream; subscribers receive the fan-out through whatever
/// stream provider the host has configured.
/// </summary>
/// <remarks>
/// <para>
/// <b>Stream identity.</b> A single stream per cluster, addressed as
/// <c>(StreamNamespace, Guid.Empty)</c>. Partitioning per agent / tenant is a
/// later refinement — v1 keeps one stream so consumers can subscribe without
/// knowing agent ids up front. When partitioning becomes useful, this class gets
/// an overload; the public surface stays additive.
/// </para>
/// <para>
/// <b>Provider choice.</b> The bus is provider-neutral: consumers wire whichever
/// Orleans stream provider their deployment risk profile allows and pass the
/// name through the constructor. Shipped convenience helpers:
/// <list type="bullet">
///   <item><c>AddMemoryStreams("vais2.agents.events")</c> — single-process dev/test.</item>
///   <item><c>Vais2.Agents.Persistence.Redis.AgenticRedisPersistenceExtensions.UseAgenticRedisStreaming</c> — cross-silo via Redis (depends on <c>Microsoft.Orleans.Streaming.Redis</c>, currently only in alpha — treat as preview-grade).</item>
///   <item><c>AddEventHubStreams("vais2.agents.events", ...)</c> — Azure production.</item>
/// </list>
/// </para>
/// <para>
/// <b>Thread-safety.</b> Publish and subscribe are safe from multiple threads
/// concurrently. Each returned <see cref="IDisposable"/> owns a single
/// <see cref="StreamSubscriptionHandle{T}"/> and unsubscribes at most once.
/// </para>
/// </remarks>
public sealed class OrleansAgentEventBus : IAgentEventBus
{
    /// <summary>
    /// Conventional stream namespace used by <see cref="OrleansAgentEventBus"/> and
    /// the matching <c>UseAgentic*Streaming</c> extensions. Constant so that all
    /// components — publisher bus, subscriber consumers, durable-provider backends —
    /// agree on the stream without stringly-typed configuration.
    /// </summary>
    public const string StreamNamespace = "vais2.agents.events";

    /// <summary>
    /// Conventional stream id. A <see cref="Guid.Empty"/> key means "the one global
    /// agent-event stream in this cluster".
    /// </summary>
    public static readonly Guid StreamKey = Guid.Empty;

    private readonly IClusterClient _clusterClient;
    private readonly string _streamProviderName;

    /// <summary>
    /// Create a bus bound to a cluster client and the name of an already-registered
    /// Orleans stream provider.
    /// </summary>
    /// <param name="clusterClient">
    /// The Orleans cluster client (on a client host) or silo provider facade (on a silo host).
    /// Silo-side code can inject <see cref="IClusterClient"/> because Orleans registers a
    /// local cluster client internally.
    /// </param>
    /// <param name="streamProviderName">
    /// Name the host passed to <c>siloBuilder.AddMemoryStreams(name)</c> /
    /// <c>AddEventHubStreams(name, ...)</c> / equivalent. Default value when using the
    /// conventional name is <see cref="StreamNamespace"/>.
    /// </param>
    public OrleansAgentEventBus(IClusterClient clusterClient, string streamProviderName)
    {
        ArgumentNullException.ThrowIfNull(clusterClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(streamProviderName);
        _clusterClient = clusterClient;
        _streamProviderName = streamProviderName;
    }

    /// <inheritdoc />
    public async ValueTask PublishAsync(AgentEvent @event, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(@event);

        var stream = GetStream();
        await stream.OnNextAsync(@event).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        // Block on the subscribe — Orleans stream providers expose a Task-returning
        // SubscribeAsync and we need a synchronous IDisposable per the contract. The
        // subscribe call is typically fast (message-pump registration), and this is
        // not a hot path (consumers subscribe at startup, not per turn).
        var stream = GetStream();
        var observer = new ObserverAdapter(handler);
        var handle = stream.SubscribeAsync(observer).GetAwaiter().GetResult();
        return new Subscription(handle);
    }

    private IAsyncStream<AgentEvent> GetStream()
    {
        var provider = _clusterClient.GetStreamProvider(_streamProviderName);
        var streamId = StreamId.Create(StreamNamespace, StreamKey);
        return provider.GetStream<AgentEvent>(streamId);
    }

    private sealed class ObserverAdapter : IAsyncObserver<AgentEvent>
    {
        private readonly Func<AgentEvent, CancellationToken, ValueTask> _handler;

        public ObserverAdapter(Func<AgentEvent, CancellationToken, ValueTask> handler)
        {
            _handler = handler;
        }

        public Task OnNextAsync(AgentEvent item, StreamSequenceToken? token = null)
            => _handler(item, CancellationToken.None).AsTask();

        public Task OnCompletedAsync() => Task.CompletedTask;

        public Task OnErrorAsync(Exception ex) => Task.CompletedTask;
    }

    private sealed class Subscription : IDisposable
    {
        private StreamSubscriptionHandle<AgentEvent>? _handle;

        public Subscription(StreamSubscriptionHandle<AgentEvent> handle)
        {
            _handle = handle;
        }

        public void Dispose()
        {
            var handle = Interlocked.Exchange(ref _handle, null);
            if (handle is not null)
            {
                // Fire-and-forget unsubscribe — the handle's UnsubscribeAsync is a
                // best-effort cleanup that the Orleans runtime may delay or batch.
                _ = handle.UnsubscribeAsync();
            }
        }
    }
}
