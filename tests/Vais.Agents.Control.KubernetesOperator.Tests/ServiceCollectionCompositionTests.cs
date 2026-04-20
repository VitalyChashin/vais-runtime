// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Vais.Agents.Control.Kubernetes;
using Xunit;

namespace Vais.Agents.Control.Kubernetes.Tests;

/// <summary>
/// Verifies <c>AddAgentKubernetesOperator</c> wires the expected services
/// without actually booting the operator against a cluster. Guards
/// against silent registration drift between the DI extension and the
/// controller/finalizer/handler graph.
/// </summary>
public sealed class ServiceCollectionCompositionTests
{
    [Fact]
    public void AddAgentKubernetesOperator_RegistersExpectedServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentKubernetesOperator(opts =>
        {
            opts.ControlPlaneBaseUrl = new Uri("https://runtime.local:443");
            opts.ControlPlaneAudience = "vais-agents-runtime";
        });

        services.Should().Contain(d => d.ServiceType == typeof(IKubernetesSecretResolver)
            && d.ImplementationType == typeof(KubernetesSecretResolver));

        services.Should().Contain(d => d.ServiceType == typeof(IAgentEntityKubernetesClient)
            && d.ImplementationType == typeof(AgentEntityKubernetesClient));

        services.Should().Contain(d => d.ServiceType == typeof(ServiceAccountTokenHandler));

        services.Should().Contain(d => d.ServiceType == typeof(TimeProvider));

        services.Should().Contain(d => d.ServiceType == typeof(IConfigureOptions<KubernetesOperatorOptions>));
    }

    [Fact]
    public void AddAgentKubernetesOperator_OptionsBind_BaseUrlFlowsThrough()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentKubernetesOperator(opts =>
        {
            opts.ControlPlaneBaseUrl = new Uri("https://runtime.example:8443");
            opts.AuthMode = KubernetesOperatorAuthMode.ClientCredentials;
            opts.TokenCacheTtl = TimeSpan.FromMinutes(3);
        });

        using var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<IOptions<KubernetesOperatorOptions>>().Value;

        resolved.ControlPlaneBaseUrl.Should().Be(new Uri("https://runtime.example:8443"));
        resolved.AuthMode.Should().Be(KubernetesOperatorAuthMode.ClientCredentials);
        resolved.TokenCacheTtl.Should().Be(TimeSpan.FromMinutes(3));
    }
}
