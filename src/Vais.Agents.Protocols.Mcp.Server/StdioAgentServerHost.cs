// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;

namespace Vais.Agents.Protocols.Mcp.Server;

/// <summary>
/// <see cref="IHostedService"/> that runs a Vais.Agents MCP server over stdio —
/// the transport Claude Desktop and other local MCP clients expect when they spawn
/// the server as a child process. Pairs with <see cref="McpAgentServerBuilder"/>
/// for the actual tool/call routing; this host wires up the transport + lifecycle.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deployment shape.</b> Add this service from <c>AddMcpAgentServerStdio</c>
/// then run the app as a console (<c>dotnet run</c>). Claude Desktop config points
/// at the compiled executable. stdin/stdout carry the JSON-RPC pipe; stderr remains
/// free for logging.
/// </para>
/// <para>
/// <b>Auth.</b> No inbound auth on stdio — the parent process spawns the server, so
/// trust is inherited from the parent. For HTTP transports see the PR 2
/// streamableHttp wiring (future).
/// </para>
/// </remarks>
public sealed class StdioAgentServerHost : BackgroundService
{
    private readonly IAgentRegistry _registry;
    private readonly IAgentLifecycleManager _lifecycle;
    private readonly McpAgentServerOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<StdioAgentServerHost> _logger;

    /// <summary>Construct a host. Resolved from DI typically; direct construction is fine for tests.</summary>
    public StdioAgentServerHost(
        IAgentRegistry registry,
        IAgentLifecycleManager lifecycle,
        McpAgentServerOptions? options = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(lifecycle);
        _registry = registry;
        _lifecycle = lifecycle;
        _options = options ?? new McpAgentServerOptions();
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<StdioAgentServerHost>();
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var serverOptions = McpAgentServerBuilder.Build(_registry, _lifecycle, _options);
        await using var transport = new StdioServerTransport(serverOptions, _loggerFactory);
        await using var server = McpServer.Create(transport, serverOptions, _loggerFactory, serviceProvider: null!);
        _logger.LogInformation("Vais.Agents MCP server listening on stdio (server name: {Name} v{Version}).", _options.Name, _options.Version);
        try
        {
            await server.RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Vais.Agents MCP stdio server shutting down.");
        }
    }
}
