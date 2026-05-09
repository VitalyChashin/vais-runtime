// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Vais.Agents.Runtime.Plugins.Container;

internal sealed class ContainerAgentShimFactory : IAgentHandlerFactory
{
    private readonly IContainerSupervisor _supervisor;
    private readonly ContainerPluginDescriptor _descriptor;
    private readonly ContainerPluginLoaderOptions _options;
    private readonly ILoggerFactory _loggerFactory;

    public string HandlerTypeName => _descriptor.HandlerTypeName;

    internal ContainerAgentShimFactory(
        IContainerSupervisor supervisor,
        ContainerPluginDescriptor descriptor,
        ContainerPluginLoaderOptions options,
        ILoggerFactory loggerFactory)
    {
        _supervisor = supervisor;
        _descriptor = descriptor;
        _options = options;
        _loggerFactory = loggerFactory;
    }

    public ValueTask<IAiAgent> CreateAsync(
        AgentManifest manifest,
        IServiceProvider serviceProvider,
        CancellationToken cancellationToken = default)
    {
        var preprocessors = serviceProvider
            .GetServices<IAgentPreprocessor>()
            .OrderBy(p => p.Order)
            .ToArray();

        var callTokenService = serviceProvider.GetRequiredService<ICallTokenService>();
        var internalBase = _options.InternalGatewayBaseUrl;

        var invokeClient = new HttpClient
        {
            BaseAddress = new Uri(_descriptor.InvokeBaseUrl),
            Timeout = TimeSpan.FromSeconds(_descriptor.InvokeTimeoutSeconds + 10),
        };

        var shim = new ContainerAgentShim(
            supervisor: _supervisor,
            invokeClient: invokeClient,
            preprocessors: preprocessors,
            manifest: manifest,
            callTokenService: callTokenService,
            internalLlmGatewayUrl: $"{internalBase}/v1/container-gateway/llm/complete",
            internalToolGatewayUrl: $"{internalBase}/v1/container-gateway/tools/invoke",
            invokeTimeoutSeconds: _descriptor.InvokeTimeoutSeconds,
            logger: _loggerFactory.CreateLogger<ContainerAgentShim>());

        return ValueTask.FromResult<IAiAgent>(shim);
    }
}
