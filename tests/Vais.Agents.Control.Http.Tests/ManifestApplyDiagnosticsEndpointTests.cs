// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net;
using System.Net.Http.Json;
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
/// v0.28: IManifestApplyDiagnosticsSink wired into the HTTP control plane.
/// Warnings recorded during Create / Update flow surface in the AgentApplyResponse.
/// </summary>
public sealed class ManifestApplyDiagnosticsEndpointTests : IAsyncLifetime
{
    private IHost _host = null!;
    private HttpClient _http = null!;
    private CapturingManifestApplyDiagnosticsSink _sink = null!;

    private static readonly AgentManifest SampleManifest = new(
        "diag-agent", "1.0",
        new AgentHandlerRef("declarative"),
        Array.Empty<ProtocolBinding>(),
        Array.Empty<ToolRef>());

    public async Task InitializeAsync()
    {
        _host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddSingleton<ICompletionProvider>(
                        new FakeCompletionProvider(_ => new CompletionResponse("ok")));
                    services.AddSingleton<IAgentRuntime>(sp =>
                        new InMemoryAgentRuntime(sp.GetRequiredService<ICompletionProvider>()));
                    services.AddSingleton<IAgentRegistry, InMemoryAgentRegistry>();
                    services.AddSingleton<IAgentLifecycleManager>(sp => new AgentLifecycleManager(
                        sp.GetRequiredService<IAgentRegistry>(),
                        sp.GetRequiredService<IAgentRuntime>()));
                    services.AddAgentControlPlane();
                    services.AddRouting();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapAgentControlPlane());
                });
            })
            .StartAsync();

        _http = _host.GetTestClient();
        _http.BaseAddress ??= new Uri("http://localhost");
        _sink = _host.Services.GetRequiredService<CapturingManifestApplyDiagnosticsSink>();
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    // Local DTOs avoid CS0433: AgentApplyResponse is declared in both the server and client
    // packages (same namespace). Using a local mirror type lets ReadFromJsonAsync resolve
    // unambiguously while keeping identical JSON deserialization semantics.
    private sealed record ApplyResponseDto(AgentHandle Handle, List<DiagnosticDto> Warnings);
    private sealed record DiagnosticDto(string Urn, string Detail);

    [Fact]
    public async Task Create_Returns_AgentApplyResponse_With_Empty_Warnings_When_No_Diagnostics()
    {
        using var response = await _http.PostAsJsonAsync("/v1/agents",
            new { apiVersion = "vais.agents/v1", kind = "Agent", metadata = new { id = "diag-agent", version = "1.0" } });

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await response.Content.ReadFromJsonAsync<ApplyResponseDto>();
        body.Should().NotBeNull();
        body!.Handle.AgentId.Should().Be("diag-agent");
        body.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_Surfaces_Warnings_Recorded_By_Sink()
    {
        // Inject a warning into the capturing sink's current async scope from outside
        // (simulates what the manifest translator does during CreateAsync).
        // Because the HTTP handler calls sink.BeginCapture() before the manager,
        // we hook into the pipeline by registering a delegating lifecycle manager
        // that records a diagnostic mid-flight.
        await _host.StopAsync();
        _host.Dispose();

        _host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddSingleton<ICompletionProvider>(
                        new FakeCompletionProvider(_ => new CompletionResponse("ok")));
                    services.AddSingleton<IAgentRuntime>(sp =>
                        new InMemoryAgentRuntime(sp.GetRequiredService<ICompletionProvider>()));
                    services.AddSingleton<IAgentRegistry, InMemoryAgentRegistry>();
                    services.AddAgentControlPlane();
                    // Register a lifecycle manager that records a warning via the sink
                    // before delegating to the real manager — simulates translator behavior.
                    services.AddSingleton<IAgentLifecycleManager>(sp =>
                    {
                        var inner = new AgentLifecycleManager(
                            sp.GetRequiredService<IAgentRegistry>(),
                            sp.GetRequiredService<IAgentRuntime>());
                        var sink = sp.GetRequiredService<IManifestApplyDiagnosticsSink>();
                        return new WarnOnCreateManager(inner, sink);
                    });
                    services.AddRouting();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapAgentControlPlane());
                });
            })
            .StartAsync();

        _http = _host.GetTestClient();
        _http.BaseAddress ??= new Uri("http://localhost");

        using var response = await _http.PostAsJsonAsync("/v1/agents",
            new { apiVersion = "vais.agents/v1", kind = "Agent", metadata = new { id = "warned-agent", version = "1.0" } });

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await response.Content.ReadFromJsonAsync<ApplyResponseDto>();
        body.Should().NotBeNull();
        body!.Handle.AgentId.Should().Be("warned-agent");
        body.Warnings.Should().ContainSingle()
            .Which.Urn.Should().Be("urn:vais-agents:test-warning");
    }

    [Fact]
    public async Task Update_Surfaces_Warnings_Recorded_By_Sink()
    {
        await _host.StopAsync();
        _host.Dispose();

        _host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddSingleton<ICompletionProvider>(
                        new FakeCompletionProvider(_ => new CompletionResponse("ok")));
                    services.AddSingleton<IAgentRuntime>(sp =>
                        new InMemoryAgentRuntime(sp.GetRequiredService<ICompletionProvider>()));
                    services.AddSingleton<IAgentRegistry, InMemoryAgentRegistry>();
                    services.AddAgentControlPlane();
                    services.AddSingleton<IAgentLifecycleManager>(sp =>
                    {
                        var inner = new AgentLifecycleManager(
                            sp.GetRequiredService<IAgentRegistry>(),
                            sp.GetRequiredService<IAgentRuntime>());
                        var sink = sp.GetRequiredService<IManifestApplyDiagnosticsSink>();
                        return new WarnOnCreateManager(inner, sink);
                    });
                    services.AddRouting();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapAgentControlPlane());
                });
            })
            .StartAsync();

        _http = _host.GetTestClient();
        _http.BaseAddress ??= new Uri("http://localhost");

        // Create first so PATCH can find the agent.
        await _http.PostAsJsonAsync("/v1/agents",
            new { apiVersion = "vais.agents/v1", kind = "Agent", metadata = new { id = "update-agent", version = "1.0" } });

        using var response = await _http.PatchAsJsonAsync("/v1/agents/update-agent",
            new { apiVersion = "vais.agents/v1", kind = "Agent", metadata = new { id = "update-agent", version = "1.1" } });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<ApplyResponseDto>();
        body.Should().NotBeNull();
        body!.Handle.Version.Should().Be("1.1");
        body.Warnings.Should().ContainSingle()
            .Which.Urn.Should().Be("urn:vais-agents:test-warning");
    }

    private sealed class WarnOnCreateManager(IAgentLifecycleManager inner, IManifestApplyDiagnosticsSink sink)
        : IAgentLifecycleManager
    {
        public async ValueTask<AgentHandle> CreateAsync(AgentManifest manifest, CancellationToken ct)
        {
            sink.Record(manifest.Id, "urn:vais-agents:test-warning", "test diagnostic detail");
            return await inner.CreateAsync(manifest, ct);
        }

        public async ValueTask<AgentHandle> UpdateAsync(AgentHandle current, AgentManifest newManifest, CancellationToken ct)
        {
            sink.Record(newManifest.Id, "urn:vais-agents:test-warning", "test diagnostic detail");
            return await inner.UpdateAsync(current, newManifest, ct);
        }

        public ValueTask<AgentStatus> QueryAsync(AgentHandle handle, CancellationToken ct) => inner.QueryAsync(handle, ct);
        public ValueTask<AgentInvocationResult> InvokeAsync(AgentHandle handle, AgentInvocationRequest request, CancellationToken ct) => inner.InvokeAsync(handle, request, ct);
        public ValueTask CancelAsync(AgentHandle handle, CancellationToken ct) => inner.CancelAsync(handle, ct);
        public ValueTask EvictAsync(AgentHandle handle, CancellationToken ct) => inner.EvictAsync(handle, ct);
        public ValueTask SignalAsync(AgentHandle handle, AgentSignal signal, CancellationToken ct) => inner.SignalAsync(handle, signal, ct);
    }

    private sealed class FakeCompletionProvider(Func<CompletionRequest, CompletionResponse> impl) : ICompletionProvider
    {
        public string ProviderName => "fake";
        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(impl(request));
    }
}
