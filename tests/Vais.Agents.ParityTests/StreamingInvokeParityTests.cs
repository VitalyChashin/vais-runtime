// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
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

namespace Vais.Agents.ParityTests;

/// <summary>
/// v0.12 PR 3: parity between the library-level <see cref="IStreamingAiAgent"/>
/// surface (observed via <see cref="StatefulAiAgent"/> directly) and the HTTP
/// SSE surface (observed via <see cref="AgentControlPlaneClient"/>). Both should
/// yield the same event kinds in the same order for the same scripted provider.
/// </summary>
public sealed class StreamingInvokeParityTests
{
    private static readonly AgentManifest TestManifest = new(
        "parity-streamer", "1.0",
        new AgentHandlerRef("declarative"),
        new[] { new ProtocolBinding("Http") },
        Array.Empty<ToolRef>());

    [Fact]
    public async Task Text_Only_Client_Matches_Concatenated_StreamAsync_Output()
    {
        var chunks = new[] { "Hello, ", "streaming ", "world!" };

        // Library-side: concatenate StatefulAiAgent.StreamAsync(string) deltas.
        var libProvider = new ScriptedStreamingProvider(chunks.Select(c => new CompletionUpdate(c)));
        var libAgent = new StatefulAiAgent(libProvider);
        var libText = new System.Text.StringBuilder();
        await foreach (var d in libAgent.StreamAsync("hi"))
        {
            libText.Append(d);
        }

        // HTTP-side: text-only client yields same deltas.
        using var host = await StartHostAsync(chunks.Select(c => new CompletionUpdate(c)).ToArray());
        var httpProvider = host.Services.GetRequiredService<ICompletionProvider>();
        var manager = host.Services.GetRequiredService<IAgentLifecycleManager>();
        await manager.CreateAsync(TestManifest);

        var client = new AgentControlPlaneClient(host.GetTestClient());
        var httpText = new System.Text.StringBuilder();
        await foreach (var d in client.InvokeStreamAsync(
            TestManifest.Id,
            new AgentInvocationRequest("hi"),
            version: null,
            idempotencyKey: null,
            cancellationToken: default))
        {
            httpText.Append(d);
        }

        httpText.ToString().Should().Be(libText.ToString());
    }

    [Fact]
    public async Task Full_Events_Client_Matches_Library_StreamEvents_Kinds_And_Order()
    {
        var chunks = new[] { "alpha ", "beta" };

        // Library-side: enumerate StatefulAiAgent's IStreamingAiAgent events.
        var libProvider = new ScriptedStreamingProvider(chunks.Select(c => new CompletionUpdate(c)));
        var libAgent = new StatefulAiAgent(libProvider);
        var libEventKinds = new List<string>();
        await foreach (var e in ((IStreamingAiAgent)libAgent).StreamAsync("hi", new AgentContext(), default))
        {
            libEventKinds.Add(e.GetType().Name);
        }

        // HTTP-side: full-events client yields same event kinds + order.
        using var host = await StartHostAsync(chunks.Select(c => new CompletionUpdate(c)).ToArray());
        var manager = host.Services.GetRequiredService<IAgentLifecycleManager>();
        await manager.CreateAsync(TestManifest);

        var client = new AgentControlPlaneClient(host.GetTestClient());
        var httpEventKinds = new List<string>();
        await foreach (var e in client.InvokeStreamEventsAsync(
            TestManifest.Id,
            new AgentInvocationRequest("hi"),
            version: null,
            idempotencyKey: null,
            cancellationToken: default))
        {
            httpEventKinds.Add(e.GetType().Name);
        }

        httpEventKinds.Should().Equal(libEventKinds);
    }

    private static Task<IHost> StartHostAsync(CompletionUpdate[] chunks)
    {
        return new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddSingleton<ICompletionProvider>(new ScriptedStreamingProvider(chunks));
                    services.AddSingleton<IAgentRuntime>(sp => new InMemoryAgentRuntime(sp.GetRequiredService<ICompletionProvider>()));
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
                    app.UseEndpoints(endpoints => endpoints.MapAgentControlPlane());
                });
            })
            .StartAsync();
    }

    private sealed class ScriptedStreamingProvider : ICompletionProvider, IStreamingCompletionProvider
    {
        private readonly IReadOnlyList<CompletionUpdate> _chunks;
        public ScriptedStreamingProvider(IEnumerable<CompletionUpdate> chunks) { _chunks = chunks.ToArray(); }
        public string ProviderName => "scripted-stream-parity";

        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

#pragma warning disable CS1998
        public async IAsyncEnumerable<CompletionUpdate> StreamAsync(CompletionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var c in _chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return c;
            }
        }
#pragma warning restore CS1998
    }
}
