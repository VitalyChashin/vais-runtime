// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging;
using Vais.Agents.Core;

namespace Vais.Agents.Runtime.Plugins.Python;

/// <summary>
/// <see cref="IAgentHandlerFactory"/> registered for each Python plugin whose
/// <c>spec.kind</c> is <c>agent-handler</c> (v0.24). One factory per plugin;
/// creates a <see cref="PythonAgentShim"/> per grain activation.
/// </summary>
internal sealed class PythonAgentShimFactory : IAgentHandlerFactory
{
    private readonly PythonSubprocessSupervisor _supervisor;
    private readonly int _maxStateSizeBytes;
    private readonly ILoggerFactory? _loggerFactory;

    internal PythonAgentShimFactory(
        PythonSubprocessSupervisor supervisor,
        int maxStateSizeBytes,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(supervisor);
        _supervisor = supervisor;
        _maxStateSizeBytes = maxStateSizeBytes;
        _loggerFactory = loggerFactory;
    }

    /// <inheritdoc />
    public string HandlerTypeName => _supervisor.Descriptor.HandlerTypeName!;

    /// <inheritdoc />
    public ValueTask<IAiAgent> CreateAsync(
        AgentManifest manifest,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var session = new InMemoryAgentSession(manifest.Id);
        var logger = _loggerFactory?.CreateLogger<PythonAgentShim>();
        var shim = new PythonAgentShim(_supervisor, session, _maxStateSizeBytes, logger);

        if (manifest.SystemPrompt?.Inline is { Length: > 0 } sysPrompt)
            shim.SystemPrompt = sysPrompt;

        return ValueTask.FromResult<IAiAgent>(shim);
    }
}
