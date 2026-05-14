// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;

namespace Vais.Agents.Runtime.Plugins;

/// <summary>
/// Auto-wrap factory synthesised by the loader for plugins that export an
/// <see cref="IAiAgent"/> implementation without a matching
/// <see cref="IAgentHandlerFactory"/>. Uses
/// <see cref="ActivatorUtilities.CreateInstance{T}"/> to DI-resolve
/// constructor dependencies.
/// </summary>
/// <remarks>
/// When the agent manifest carries a <c>ModelSpec</c>, the caller
/// (<c>AgentManifestTranslator</c>) pre-builds <c>ICompletionProvider</c>
/// and wraps the <c>serviceProvider</c> argument so that the constructor
/// dependency is satisfied without making <c>ICompletionProvider</c> a
/// container singleton.
/// </remarks>
/// <typeparam name="TAgent">Concrete <see cref="IAiAgent"/> type declared by the plugin.</typeparam>
internal sealed class DefaultHandlerFactory<TAgent> : IAgentHandlerFactory
    where TAgent : IAiAgent
{
    public DefaultHandlerFactory(string handlerTypeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handlerTypeName);
        HandlerTypeName = handlerTypeName;
    }

    /// <inheritdoc />
    public string HandlerTypeName { get; }

    /// <inheritdoc />
    public ValueTask<IAiAgent> CreateAsync(
        AgentManifest manifest,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(serviceProvider);
        cancellationToken.ThrowIfCancellationRequested();

        return new ValueTask<IAiAgent>(ActivatorUtilities.CreateInstance<TAgent>(serviceProvider));
    }
}

/// <summary>
/// Non-generic helper so the loader can construct the generic factory given
/// a <see cref="Type"/> discovered via reflection.
/// </summary>
internal static class DefaultHandlerFactory
{
    public static IAgentHandlerFactory Create(Type agentType, string handlerTypeName)
    {
        ArgumentNullException.ThrowIfNull(agentType);
        ArgumentException.ThrowIfNullOrWhiteSpace(handlerTypeName);

        if (!typeof(IAiAgent).IsAssignableFrom(agentType))
        {
            throw new ArgumentException(
                $"Type '{agentType.FullName}' does not implement IAiAgent.",
                nameof(agentType));
        }

        var factoryType = typeof(DefaultHandlerFactory<>).MakeGenericType(agentType);
        return (IAgentHandlerFactory)Activator.CreateInstance(factoryType, handlerTypeName)!;
    }
}
