// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net;
using A2A;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Vais.Agents.Control;
using Vais.Agents.Control.InProcess;
using Vais.Agents.Core;
using Vais.Agents.Hosting.InMemory;
using Xunit;

namespace Vais.Agents.Protocols.A2A.Server.Tests;

/// <summary>
/// v0.8 PR 1 HTTP integration: route mounting, AgentCard discovery, and unary
/// <c>message/send</c> round-trip via the SDK's <see cref="A2AClient"/>. Uses
/// ASP.NET Core TestHost so the JSON-RPC wire layer (owned by A2A.AspNetCore)
/// is exercised end-to-end without spinning up sockets.
/// </summary>
public sealed class A2AAgentServerHttpTests : IAsyncLifetime
{
    private IHost _host = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _host = await BuildHost();
        _http = _host.GetTestClient();
        _http.BaseAddress ??= new Uri("http://localhost");
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task WellKnown_AgentCard_Is_Served_At_Expected_Path()
    {
        using var response = await _http.GetAsync("/agents/echo/.well-known/agent-card.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\"name\":\"echo\"");
        body.Should().Contain("/agents/echo");
        body.Should().Contain("\"invoke\"");
    }

    [Fact]
    public async Task Unknown_Agent_WellKnown_Returns_404()
    {
        using var response = await _http.GetAsync("/agents/ghost/.well-known/agent-card.json");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Message_Send_Unary_Round_Trips_Agent_Reply()
    {
        var client = new A2AClient(new Uri("http://localhost/agents/echo"), _http);

        var message = new Message
        {
            Role = Role.User,
            MessageId = Guid.NewGuid().ToString("N"),
        };
        message.Parts.Add(Part.FromText("ping"));

        var response = await client.SendMessageAsync(new SendMessageRequest { Message = message }, CancellationToken.None);

        response.PayloadCase.Should().Be(SendMessageResponseCase.Message);
        response.Message!.Role.Should().Be(Role.Agent);
        var reply = string.Concat(response.Message.Parts
            .Where(p => p.ContentCase == PartContentCase.Text)
            .Select(p => p.Text));
        reply.Should().Be("echo-reply");
    }

    [Fact]
    public async Task Message_ContextId_Threads_Through_To_SessionId()
    {
        string? seenSessionId = null;
        using var host = await BuildHost(invocationObserver: req => seenSessionId = req.SessionId);
        var client = new A2AClient(new Uri("http://localhost/agents/echo"), host.GetTestClient());

        var message = new Message
        {
            Role = Role.User,
            MessageId = Guid.NewGuid().ToString("N"),
            ContextId = "ctx-42",
        };
        message.Parts.Add(Part.FromText("hi"));

        await client.SendMessageAsync(new SendMessageRequest { Message = message }, CancellationToken.None);

        seenSessionId.Should().Be("ctx-42");
        await host.StopAsync();
    }

    private static Task<IHost> BuildHost(Action<AgentInvocationRequest>? invocationObserver = null) =>
        new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddSingleton<ICompletionProvider>(new FakeCompletionProvider(_ => new CompletionResponse("echo-reply")));
                    services.AddSingleton<IAgentRuntime>(sp => new InMemoryAgentRuntime(sp.GetRequiredService<ICompletionProvider>()));
                    services.AddSingleton<IAgentRegistry, InMemoryAgentRegistry>();
                    services.AddSingleton<IAgentLifecycleManager>(sp =>
                    {
                        var lifecycle = new AgentLifecycleManager(
                            sp.GetRequiredService<IAgentRegistry>(),
                            sp.GetRequiredService<IAgentRuntime>());
                        return invocationObserver is null
                            ? lifecycle
                            : new ObservingLifecycleManager(lifecycle, invocationObserver);
                    });
                    services.AddRouting();
                    services.AddA2AAgentServer();
                });
                web.Configure(app =>
                {
                    // Register the echo agent BEFORE the endpoint builder walks the registry.
                    var registry = app.ApplicationServices.GetRequiredService<IAgentRegistry>();
                    var lifecycle = app.ApplicationServices.GetRequiredService<IAgentLifecycleManager>();
                    lifecycle.CreateAsync(new AgentManifest(
                        "echo", "1.0",
                        new AgentHandlerRef("declarative"),
                        new[] { new ProtocolBinding("A2A") },
                        Array.Empty<ToolRef>())).GetAwaiter().GetResult();

                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapA2AAgentServer("http://localhost");
                    });
                });
            })
            .StartAsync();

    private sealed class FakeCompletionProvider(Func<CompletionRequest, CompletionResponse> impl) : ICompletionProvider
    {
        public string ProviderName => "fake";
        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(impl(request));
    }

    private sealed class ObservingLifecycleManager(IAgentLifecycleManager inner, Action<AgentInvocationRequest> observe) : IAgentLifecycleManager
    {
        public ValueTask<AgentHandle> CreateAsync(AgentManifest manifest, CancellationToken cancellationToken = default)
            => inner.CreateAsync(manifest, cancellationToken);
        public ValueTask<AgentInvocationResult> InvokeAsync(AgentHandle handle, AgentInvocationRequest request, CancellationToken cancellationToken = default)
        {
            observe(request);
            return inner.InvokeAsync(handle, request, cancellationToken);
        }
        public ValueTask SignalAsync(AgentHandle handle, AgentSignal signal, CancellationToken cancellationToken = default) => inner.SignalAsync(handle, signal, cancellationToken);
        public ValueTask<AgentStatus> QueryAsync(AgentHandle handle, CancellationToken cancellationToken = default) => inner.QueryAsync(handle, cancellationToken);
        public ValueTask CancelAsync(AgentHandle handle, CancellationToken cancellationToken = default) => inner.CancelAsync(handle, cancellationToken);
        public ValueTask<AgentHandle> UpdateAsync(AgentHandle handle, AgentManifest newManifest, CancellationToken cancellationToken = default) => inner.UpdateAsync(handle, newManifest, cancellationToken);
        public ValueTask EvictAsync(AgentHandle handle, CancellationToken cancellationToken = default) => inner.EvictAsync(handle, cancellationToken);
    }
}
