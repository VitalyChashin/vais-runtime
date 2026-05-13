// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Vais.Plugin.Sdk.Tests;

/// <summary>
/// Lightweight test harness that boots a <see cref="PluginHostBuilder"/>-wired <see cref="TestServer"/>
/// so unit tests can exercise all four plugin endpoints without a live network or container runtime.
/// </summary>
public sealed class SdkTestHarness<TAgent> : IAsyncDisposable
    where TAgent : ContainerPluginAgent, new()
{
    private readonly WebApplication _app;

    /// <summary>HTTP client configured against the in-process test server.</summary>
    public HttpClient Client { get; }

    private SdkTestHarness(WebApplication app, HttpClient client)
    {
        _app = app;
        Client = client;
    }

    /// <summary>
    /// Creates and starts a harness with <typeparamref name="TAgent"/> registered as the plugin agent.
    /// </summary>
    /// <param name="configureServices">
    /// Optional callback to override DI registrations (e.g. inject <see cref="ILlmGatewayClient"/> mocks).
    /// </param>
    public static async Task<SdkTestHarness<TAgent>> CreateAsync(
        Action<IServiceCollection>? configureServices = null)
    {
        var builder = PluginHost.CreateBuilder(["--environment", "Test"]);
        builder.AddPlugin<TAgent>(targetApiVersion: "0.24");
        builder.ApplicationBuilder.WebHost.UseTestServer();
        configureServices?.Invoke(builder.Services);
        var app = builder.Build();
        await app.StartAsync().ConfigureAwait(false);
        var client = app.GetTestClient();
        return new SdkTestHarness<TAgent>(app, client);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }
}
