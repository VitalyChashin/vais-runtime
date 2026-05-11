// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using Vais.Agents.Protocols.Mcp;

namespace Vais.Agents.Control.Mcp;

/// <summary>
/// Hosted service that bridges <see cref="IMcpServerRegistry"/> physical entries to live
/// <see cref="McpClient"/> connections, exposing them as an <see cref="INamedToolSourceProvider"/>
/// for <c>AgentManifestTranslator</c> to consume at agent activation time.
/// </summary>
/// <remarks>
/// <para>
/// <b>Transports.</b> Supports <c>streamableHttp</c> and <c>sse</c>. Virtual servers and
/// other transports (<c>stdio</c>, <c>plugin</c>, <c>registered</c>) are ignored.
/// </para>
/// <para>
/// <b>Scaling contract (P5).</b> Connections are per-silo — each silo opens its own
/// <see cref="McpClient"/> instances. A server reachable from silo A but not silo B
/// returns null from <see cref="GetByName"/> on silo B, causing
/// <c>McpServerUnavailable</c> at agent activation on that silo. This is an accepted
/// known limitation and not a bug.
/// </para>
/// <para>
/// <b>Reconnect.</b> The service retries every 30 seconds. On reconnect,
/// registered <see cref="IMcpServerConnectionChangedHook"/> implementations are invoked
/// so the manifest-translator cache can be invalidated for affected agents.
/// </para>
/// </remarks>
internal sealed class PhysicalMcpConnectionService : BackgroundService, INamedToolSourceProvider
{
    private const string StreamableHttp = "streamableHttp";
    private const string Sse = "sse";

    private sealed class ConnectionEntry(IAsyncDisposable transport, McpClient client) : IAsyncDisposable
    {
        public McpToolSource Source { get; } = new(client);

        public async ValueTask DisposeAsync()
        {
            try { await client.DisposeAsync().ConfigureAwait(false); } catch { /* best-effort */ }
            try { await transport.DisposeAsync().ConfigureAwait(false); } catch { /* best-effort */ }
        }
    }

    private readonly IMcpServerRegistry _registry;
    private readonly Func<IEnumerable<IMcpServerConnectionChangedHook>> _hooksFactory;
    private readonly ILogger<PhysicalMcpConnectionService> _logger;

    // null value = server tracked but not yet connected / reconnecting
    private readonly ConcurrentDictionary<string, ConnectionEntry?> _connections =
        new(StringComparer.Ordinal);

    // Used by tests — hooks resolved eagerly from a known-complete collection.
    internal PhysicalMcpConnectionService(
        IMcpServerRegistry registry,
        IEnumerable<IMcpServerConnectionChangedHook> hooks,
        ILogger<PhysicalMcpConnectionService>? logger = null)
    {
        _registry = registry;
        var captured = hooks.ToArray();
        _hooksFactory = () => captured;
        _logger = logger ?? NullLogger<PhysicalMcpConnectionService>.Instance;
    }

    // Used by DI — hooks resolved lazily via IServiceProvider to avoid a circular
    // dependency: PhysicalMcpConnectionService → McpTranslatorInvalidationHook
    // → AgentManifestTranslator → INamedToolSourceProvider (PhysicalMcpConnectionService).
    internal PhysicalMcpConnectionService(
        IMcpServerRegistry registry,
        IServiceProvider serviceProvider,
        ILogger<PhysicalMcpConnectionService>? logger = null)
    {
        _registry = registry;
        _hooksFactory = () => serviceProvider.GetServices<IMcpServerConnectionChangedHook>();
        _logger = logger ?? NullLogger<PhysicalMcpConnectionService>.Instance;
    }

    /// <inheritdoc />
    public IToolSource? GetByName(string name)
    {
        _connections.TryGetValue(name, out var entry);
        return entry?.Source;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial scan: register all physical servers, fire connection attempts concurrently.
        await foreach (var srv in _registry.ListAsync(ct: stoppingToken).ConfigureAwait(false))
        {
            if (!IsSupported(srv)) continue;
            _connections.TryAdd(srv.Id, null);
            _ = TryConnectOnceAsync(srv, stoppingToken);
        }

        // Reconnect loop: every 30 s, retry disconnected servers and pick up new registrations.
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await foreach (var srv in _registry.ListAsync(ct: stoppingToken).ConfigureAwait(false))
            {
                if (!IsSupported(srv)) continue;
                _connections.TryAdd(srv.Id, null);
                if (_connections[srv.Id] is null)
                    _ = TryConnectOnceAsync(srv, stoppingToken);
            }
        }
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        foreach (var (serverId, entry) in _connections)
        {
            if (entry is null) continue;
            await DispatchHooksAsync(h => h.OnDisconnectedAsync(serverId, cancellationToken), serverId).ConfigureAwait(false);
            await entry.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task TryConnectOnceAsync(McpServerManifest srv, CancellationToken ct)
    {
        try
        {
            // Dispose any previous connection before replacing it.
            if (_connections.TryGetValue(srv.Id, out var old) && old is not null)
            {
                _connections[srv.Id] = null;
                await DispatchHooksAsync(h => h.OnDisconnectedAsync(srv.Id, ct), srv.Id).ConfigureAwait(false);
                await old.DisposeAsync().ConfigureAwait(false);
            }

            var (transport, client) = await OpenConnectionAsync(srv, ct).ConfigureAwait(false);
            var entry = new ConnectionEntry(transport, client);
            _connections[srv.Id] = entry;

            _logger.LogInformation(
                "MCP server connected. ServerId={ServerId} Transport={Transport}",
                srv.Id, srv.Transport);

            await DispatchHooksAsync(h => h.OnConnectedAsync(srv.Id, ct), srv.Id).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Clean shutdown — no log needed.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "MCP server connection failed. ServerId={ServerId} Transport={Transport} Url={Url}",
                srv.Id, srv.Transport, srv.Url);
        }
    }

    private static async Task<(IAsyncDisposable Transport, McpClient Client)> OpenConnectionAsync(
        McpServerManifest srv, CancellationToken ct)
    {
        var mode = srv.Transport == Sse ? HttpTransportMode.Sse : HttpTransportMode.StreamableHttp;
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(srv.Url!), TransportMode = mode });
        try
        {
            var client = await McpClient.CreateAsync(transport, cancellationToken: ct).ConfigureAwait(false);
            return (transport, client);
        }
        catch
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static bool IsSupported(McpServerManifest srv)
        => !srv.Virtual
            && srv.Transport is StreamableHttp or Sse
            && srv.Url is not null;

    private async Task DispatchHooksAsync(
        Func<IMcpServerConnectionChangedHook, Task> action, string serverId)
    {
        foreach (var hook in _hooksFactory())
        {
            try
            {
                await action(hook).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex,
                    "MCP connection hook {HookType} threw for server {ServerId}",
                    hook.GetType().Name, serverId);
            }
        }
    }
}
