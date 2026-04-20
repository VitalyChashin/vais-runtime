// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Vais.Agents.Control.Policy.Opa.IntegrationTests;

/// <summary>
/// Hand-rolled Testcontainers wrapper for <c>openpolicyagent/opa</c>.
/// NuGet has no shipped <c>Testcontainers.Opa</c> module (verified
/// 2026-04-20), so we compose the generic
/// <see cref="ContainerBuilder"/> directly. Pinned to OPA 1.15.2.
/// </summary>
internal sealed class OpaContainer : IAsyncDisposable
{
    private const string Image = "openpolicyagent/opa:1.15.2";
    private const int OpaPort = 8181;

    private readonly IContainer _container;

    public OpaContainer(string regoPolicyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(regoPolicyPath);
        if (!File.Exists(regoPolicyPath))
        {
            throw new FileNotFoundException($"Rego policy file not found: {regoPolicyPath}", regoPolicyPath);
        }

        _container = new ContainerBuilder(Image)
            .WithPortBinding(OpaPort, assignRandomHostPort: true)
            .WithResourceMapping(regoPolicyPath, "/policies/policy.rego")
            .WithCommand("run", "--server", "--addr", $":{OpaPort}", "/policies/policy.rego")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(OpaPort).ForPath("/health")))
            .Build();
    }

    /// <summary>Base URL the adapter's <c>OpaPolicyEngineOptions.BaseUrl</c> should point at.</summary>
    public Uri BaseUrl => new($"http://{_container.Hostname}:{_container.GetMappedPublicPort(OpaPort)}");

    public Task StartAsync(CancellationToken ct = default) => _container.StartAsync(ct);

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync().ConfigureAwait(false);
    }
}
