// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Vais.Agents.Core;

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

        // The HttpClient total timeout is the invoke's ABSOLUTE bound. In session mode that is
        // sessionTtlSeconds (so a long invoke is allowed without inflating the kill-timeout); otherwise
        // it stays invokeTimeoutSeconds. Idle/progress reclaim on the streaming path is enforced
        // separately by the shim's idle watchdog (invokeIdleTimeoutSeconds).
        var absoluteTimeoutSeconds = _descriptor.SessionTtlSeconds ?? _descriptor.InvokeTimeoutSeconds;
        var invokeClient = new HttpClient
        {
            BaseAddress = new Uri(_descriptor.InvokeBaseUrl),
            Timeout = TimeSpan.FromSeconds(absoluteTimeoutSeconds + 10),
        };

        ContainerSessionTokenConfig? sessionConfig = _descriptor.SessionTtlSeconds is { } sessionTtl
            ? new ContainerSessionTokenConfig(
                SessionTtlSeconds: sessionTtl,
                RenewTokenTtlSeconds: _options.RenewTokenTtlSeconds,
                RenewTokenUrl: $"{internalBase}/v1/container-gateway/token/renew",
                LeaseStore: serviceProvider.GetRequiredService<IInvokeLeaseStore>())
            : null;

        // G4: the shim reads IAgentContextAccessor.Current at mint time in AskAsync to forward the
        // calling grain's AgentContext claims via the call-token. Nullable so test rigs that don't
        // register an accessor still construct successfully (the shim falls back to a minimal
        // header-derived context — same as the pre-G4 behavior).
        var contextAccessor = serviceProvider.GetService<IAgentContextAccessor>();

        // G6: the shim resolves PerAgentChains.Input via IAgentManifestTranslator at invoke time
        // to run input middleware on the raw user message before sending to the plugin
        // (P12 §1 — runtime owns input shaping). Nullable for test rigs without a translator.
        var translator = serviceProvider.GetService<Vais.Agents.Runtime.Instantiation.IAgentManifestTranslator>();

        var shim = new ContainerAgentShim(
            supervisor: _supervisor,
            invokeClient: invokeClient,
            preprocessors: preprocessors,
            manifest: manifest,
            callTokenService: callTokenService,
            internalLlmGatewayUrl: $"{internalBase}/v1/container-gateway/llm/complete",
            internalToolGatewayUrl: $"{internalBase}/v1/container-gateway/tools/invoke",
            invokeTimeoutSeconds: _descriptor.InvokeTimeoutSeconds,
            sessionConfig: sessionConfig,
            invokeIdleTimeoutSeconds: _descriptor.InvokeIdleTimeoutSeconds,
            contextAccessor: contextAccessor,
            translator: translator,
            logger: _loggerFactory.CreateLogger<ContainerAgentShim>());

        return ValueTask.FromResult<IAiAgent>(shim);
    }
}
