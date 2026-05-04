// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Vais.Agents.Observability.RunStore;

/// <summary>
/// Optional hosted service that mirrors graph events to Redis Streams for live SSE tailing.
/// Each run gets a stream at <c>vais:run:{runId}:events</c>, capped at 2000 entries (MAXLEN ~).
/// Wire up via <c>AddRunStoreRedisStream()</c>.
/// </summary>
internal sealed class RedisRunStreamSink : IHostedService
{
    private readonly IAgentGraphEventBus _bus;
    private readonly string _redisConnectionString;
    private readonly ILogger<RedisRunStreamSink> _logger;
    private IDisposable? _subscription;
    private IConnectionMultiplexer? _redis;

    private const int StreamMaxLen = 2000;

    public RedisRunStreamSink(
        IAgentGraphEventBus bus,
        string redisConnectionString,
        ILogger<RedisRunStreamSink> logger)
    {
        _bus = bus;
        _redisConnectionString = redisConnectionString;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _redis = await ConnectionMultiplexer.ConnectAsync(_redisConnectionString).ConfigureAwait(false);
        _subscription = _bus.Subscribe(HandleAsync);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _redis?.Dispose();
        return Task.CompletedTask;
    }

    private async ValueTask HandleAsync(AgentGraphEvent evt, CancellationToken ct)
    {
        if (_redis is null) return;

        var db = _redis.GetDatabase();
        var key = $"vais:run:{evt.RunId}:events";
        var kind = EventKindName(evt);

        var fields = new NameValueEntry[]
        {
            new("kind", kind),
            new("step", evt.SuperStep),
            new("at", evt.At.ToUnixTimeMilliseconds()),
        };

        try
        {
            await db.StreamAddAsync(key, fields, maxLength: StreamMaxLen, useApproximateMaxLength: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis stream add failed for run {RunId} event {Kind}.", evt.RunId, kind);
        }
    }

    private static string EventKindName(AgentGraphEvent evt) => evt switch
    {
        GraphStarted => "graph.started",
        NodeStarted => "node.started",
        NodeAgentInvoked => "node.agent.invoked",
        NodeCompleted => "node.completed",
        EdgeTraversed => "edge.traversed",
        StateUpdated => "state.updated",
        GraphInterrupted => "graph.interrupted",
        GraphResumed => "graph.resumed",
        GraphCompleted => "graph.completed",
        GraphFailed => "graph.failed",
        _ => evt.GetType().Name,
    };
}
