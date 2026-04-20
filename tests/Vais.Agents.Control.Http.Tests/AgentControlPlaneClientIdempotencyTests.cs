// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Vais.Agents.Control;
using Vais.Agents.Control.Http;
using Vais.Agents.Control.InProcess;
using Vais.Agents.Core;
using Vais.Agents.Hosting.InMemory;
using Xunit;

namespace Vais.Agents.Control.Http.Tests;

/// <summary>
/// v0.11 PR 2: client-side <c>Idempotency-Key</c> threading. Covers the 5
/// scenarios enumerated in the pillar plan — explicit key forwarded, auto-gen
/// when opted in, no header in default mode, end-to-end replay, mismatch
/// surfacing as <see cref="AgentControlPlaneException"/>.
/// </summary>
public sealed class AgentControlPlaneClientIdempotencyTests
{
    private static readonly AgentManifest SampleManifest = new(
        "client-smoke", "1.0",
        new AgentHandlerRef("declarative"),
        new[] { new ProtocolBinding("Http") },
        Array.Empty<ToolRef>());

    // ---- 1: explicit key forwarded on header ----

    [Fact]
    public async Task Explicit_IdempotencyKey_Is_Forwarded()
    {
        using var host = await StartHostAsync();
        var client = new AgentControlPlaneClient(host.GetTestClient());

        await client.CreateAsync(SampleManifest, idempotencyKey: "my-explicit-key", cancellationToken: default);

        var recorder = host.Services.GetRequiredService<HeaderRecorder>();
        recorder.LastIdempotencyKey.Should().Be("my-explicit-key");
    }

    // ---- 2: auto-gen when opted in ----

    [Fact]
    public async Task AutoGen_Enabled_Sends_Generated_Key_When_Caller_Omits_One()
    {
        using var host = await StartHostAsync();
        var options = new AgentControlPlaneClientOptions { AutoGenerateIdempotencyKey = true };
        var client = new AgentControlPlaneClient(host.GetTestClient(), options);

        await client.CreateAsync(SampleManifest);

        var recorder = host.Services.GetRequiredService<HeaderRecorder>();
        recorder.LastIdempotencyKey.Should().NotBeNullOrEmpty();
        recorder.LastIdempotencyKey!.Length.Should().BeGreaterThanOrEqualTo(16, "auto-gen uses Guid.NewGuid().ToString(\"N\") by default");
    }

    // ---- 3: no header by default ----

    [Fact]
    public async Task Default_Mode_Sends_No_Header()
    {
        using var host = await StartHostAsync();
        var client = new AgentControlPlaneClient(host.GetTestClient());

        await client.CreateAsync(SampleManifest);

        var recorder = host.Services.GetRequiredService<HeaderRecorder>();
        recorder.LastIdempotencyKey.Should().BeNull();
    }

    // ---- 4: replay round-trip through the middleware ----

    [Fact]
    public async Task Replay_Round_Trip_Via_Middleware()
    {
        using var host = await StartHostAsync(mountIdempotency: true);
        var client = new AgentControlPlaneClient(host.GetTestClient());

        var first = await client.CreateAsync(SampleManifest, idempotencyKey: "replay-key", cancellationToken: default);
        var second = await client.CreateAsync(SampleManifest, idempotencyKey: "replay-key", cancellationToken: default);

        first.AgentId.Should().Be(SampleManifest.Id);
        second.AgentId.Should().Be(SampleManifest.Id);
        // Both calls deserialise to the same handle shape; the server-side dedupe is
        // proven by the registry containing exactly one manifest after two requests.
        var registry = host.Services.GetRequiredService<IAgentRegistry>();
        var manifests = new List<AgentManifest>();
        await foreach (var m in registry.ListAsync()) manifests.Add(m);
        manifests.Should().ContainSingle();
    }

    // ---- 5: mismatch surfaces as AgentControlPlaneException ----

    [Fact]
    public async Task Mismatch_Surfaces_As_AgentControlPlaneException()
    {
        using var host = await StartHostAsync(mountIdempotency: true);
        var client = new AgentControlPlaneClient(host.GetTestClient());
        const string key = "client-mismatch-key";

        await client.CreateAsync(SampleManifest, idempotencyKey: key, cancellationToken: default);

        var act = async () => await client.CreateAsync(
            SampleManifest with { Id = "different-id" },
            idempotencyKey: key,
            cancellationToken: default);

        var thrown = await act.Should().ThrowAsync<AgentControlPlaneException>();
        thrown.Which.StatusCode.Should().Be((int)HttpStatusCode.UnprocessableEntity);
        thrown.Which.Type.Should().Be(ProblemDetailsMapping.IdempotencyMismatchType);
    }

    // ---- helpers ----

    private static Task<IHost> StartHostAsync(bool mountIdempotency = false)
    {
        return new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddSingleton<ICompletionProvider>(new FakeCompletionProvider(_ => new CompletionResponse("hi")));
                    services.AddSingleton<IAgentRuntime>(sp => new InMemoryAgentRuntime(sp.GetRequiredService<ICompletionProvider>()));
                    services.AddSingleton<IAgentRegistry, InMemoryAgentRegistry>();
                    services.AddSingleton<IAgentLifecycleManager>(sp => new AgentLifecycleManager(
                        sp.GetRequiredService<IAgentRegistry>(),
                        sp.GetRequiredService<IAgentRuntime>()));
                    services.AddAgentControlPlane();
                    services.AddSingleton<HeaderRecorder>();
                    if (mountIdempotency)
                    {
                        services.AddAgentControlPlaneIdempotency(options =>
                        {
                            options.EvictionInterval = TimeSpan.Zero;
                        });
                    }
                    services.AddRouting();
                });
                web.Configure(app =>
                {
                    // Record the header on every request before anything else sees it.
                    app.Use(async (ctx, next) =>
                    {
                        var recorder = ctx.RequestServices.GetRequiredService<HeaderRecorder>();
                        recorder.LastIdempotencyKey = ctx.Request.Headers.TryGetValue("Idempotency-Key", out var values)
                            ? values.ToString()
                            : null;
                        await next();
                    });
                    app.UseRouting();
                    if (mountIdempotency)
                    {
                        app.UseAgentControlPlaneIdempotency();
                    }
                    app.UseEndpoints(endpoints => endpoints.MapAgentControlPlane());
                });
            })
            .StartAsync();
    }

    private sealed class HeaderRecorder
    {
        public string? LastIdempotencyKey { get; set; }
    }

    private sealed class FakeCompletionProvider(Func<CompletionRequest, CompletionResponse> impl) : ICompletionProvider
    {
        public string ProviderName => "fake";
        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(impl(request));
    }
}
