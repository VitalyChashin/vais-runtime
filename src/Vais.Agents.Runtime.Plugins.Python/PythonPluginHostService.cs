// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vais.Agents.Protocols.Mcp;

namespace Vais.Agents.Runtime.Plugins.Python;

/// <summary>
/// <see cref="IHostedService"/> that scans the plugins directory, spawns one supervised
/// subprocess per Python plugin, performs the MCP handshake, and makes loaded plugins
/// available via <see cref="IPythonPluginHost.LoadedPlugins"/>. Non-fatal per-plugin
/// failures are logged and skipped; <c>StartAsync</c> returns successfully even when some
/// plugins fail to load.
/// </summary>
internal sealed class PythonPluginHostService : IPythonPluginHost, IHostedService, INamedToolSourceProvider
{
    // At most 4 parallel spawns to avoid fork storms on pods with tight CPU limits.
    private static readonly int MaxSpawnParallelism = Math.Min(Environment.ProcessorCount, 4);

    private readonly PythonPluginLoaderOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<PythonPluginHostService> _logger;
    private readonly Func<PythonPluginDescriptor, PythonSubprocessSupervisor> _supervisorFactory;
    private readonly IPluginHandlerRegistry? _handlerRegistry;

    private readonly List<PythonSubprocessSupervisor> _supervisors = [];

    internal PythonPluginHostService(
        PythonPluginLoaderOptions? options = null,
        ILoggerFactory? loggerFactory = null,
        IPluginHandlerRegistry? handlerRegistry = null)
        : this(options, loggerFactory, supervisorFactory: null, handlerRegistry) { }

    // Test constructor — inject a custom supervisor factory (handlerRegistry optional).
    internal PythonPluginHostService(
        PythonPluginLoaderOptions? options,
        ILoggerFactory? loggerFactory,
        Func<PythonPluginDescriptor, PythonSubprocessSupervisor>? supervisorFactory,
        IPluginHandlerRegistry? handlerRegistry = null)
    {
        _options = options ?? new PythonPluginLoaderOptions();
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<PythonPluginHostService>();
        _handlerRegistry = handlerRegistry;
        _supervisorFactory = supervisorFactory
            ?? (d => new PythonSubprocessSupervisor(d, _loggerFactory));
    }

    // -------------------------------------------------------------------------
    // IPythonPluginHost
    // -------------------------------------------------------------------------

    /// <inheritdoc />
    public IReadOnlyCollection<LoadedPythonPlugin> LoadedPlugins
    {
        get
        {
            lock (_supervisors)
                return _supervisors
                    .Select(s => new LoadedPythonPlugin(s.Descriptor, s.Status, s.ProcessId, s.McpClient))
                    .ToList();
        }
    }

    // -------------------------------------------------------------------------
    // INamedToolSourceProvider
    // -------------------------------------------------------------------------

    /// <inheritdoc />
    public IToolSource? GetByName(string name)
    {
        lock (_supervisors)
        {
            var supervisor = _supervisors.FirstOrDefault(
                s => string.Equals(s.Descriptor.Name, name, StringComparison.Ordinal));
            if (supervisor is null || supervisor.Status != PythonPluginStatus.Ready)
                return null;
            return new McpToolSource(supervisor.McpClient!);
        }
    }

    // -------------------------------------------------------------------------
    // IHostedService
    // -------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var scanner = new PythonPluginScanner(_options, _loggerFactory.CreateLogger<PythonPluginScanner>());
        var descriptors = scanner.Scan();

        if (descriptors.Count == 0)
        {
            _logger.LogInformation("No Python plugins found in '{Dir}'.", _options.PluginsDirectory);
            return;
        }

        _logger.LogInformation(
            "Starting {Count} Python plugin(s) from '{Dir}' (max spawn parallelism: {P}).",
            descriptors.Count, _options.PluginsDirectory, MaxSpawnParallelism);

        // Spawn + handshake with bounded parallelism.
        using var semaphore = new SemaphoreSlim(MaxSpawnParallelism, MaxSpawnParallelism);
        var startTasks = descriptors.Select(async descriptor =>
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var supervisor = _supervisorFactory(descriptor);
                lock (_supervisors) _supervisors.Add(supervisor);
                supervisor.Start();
                await supervisor.InitialHandshakeTask.ConfigureAwait(false);

                // Register agent-handler factory after successful handshake (v0.24).
                if (descriptor.HandlerKind == PythonHandlerKind.AgentHandler &&
                    supervisor.Status == PythonPluginStatus.Ready &&
                    _handlerRegistry is { } registry)
                {
                    try
                    {
                        var factory = new PythonAgentShimFactory(
                            supervisor,
                            _options.MaxAgentStateSizeBytes,
                            _loggerFactory);
                        registry.Register(factory, descriptor.Name);
                        _logger.LogInformation(
                            "Registered Python agent handler '{TypeName}' for plugin '{Name}'.",
                            descriptor.HandlerTypeName, descriptor.Name);
                    }
                    catch (PluginLoadException ex)
                    {
                        _logger.LogWarning(ex,
                            "[{Urn}] Python agent handler '{TypeName}' collides with an existing " +
                            "registration — plugin '{Name}' marked unavailable.",
                            PythonPluginUrns.AgentHandlerCollision,
                            descriptor.HandlerTypeName,
                            descriptor.Name);
                    }
                }
            }
            finally
            {
                semaphore.Release();
            }
        }).ToArray();

        await Task.WhenAll(startTasks).ConfigureAwait(false);

        var ready = _supervisors.Count(s => s.Status == PythonPluginStatus.Ready);
        var unavailable = _supervisors.Count - ready;

        _logger.LogInformation(
            "Python plugin startup complete — {Ready} ready, {Unavailable} unavailable.",
            ready, unavailable);
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_supervisors.Count == 0)
            return;

        _logger.LogInformation("Stopping {Count} Python plugin supervisor(s).", _supervisors.Count);

        var stopTasks = _supervisors.Select(s => s.StopAsync()).ToArray();
        await Task.WhenAll(stopTasks).ConfigureAwait(false);

        _logger.LogInformation("All Python plugin supervisors stopped.");
    }
}
