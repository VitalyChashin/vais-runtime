// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Vais.Agents.Runtime.Extensions.Container;
using Xunit;

namespace Vais.Agents.Runtime.Extensions.Conformance;

/// <summary>
/// Container-host invocation conformance.
/// Inherits the host-agnostic registry/composer assertions from <see cref="ExtensionConformanceBase"/>
/// and adds container-specific invocation tests that exercise the HTTP proxy path using
/// an in-process mock server — no real Docker daemon required.
/// </summary>
public sealed class ContainerExtensionConformanceTests : ExtensionConformanceBase
{
    // ── ExtensionConformanceBase factory ───────────────────────────────────

    protected override Task<ExtensionDescriptor> CreateDescriptorAsync(
        string extensionId, ExtensionScope? scope, int priority)
    {
        // For base-class registry/composer tests we only need a valid HandlerBinding.
        // The handler instance is a no-op proxy backed by a dummy URI; these tests never invoke it.
        var manifest = MakeManifest(extensionId, "1.0.0", scope);
        var proxy = new HttpContainerHandlerProxy(
            new HttpClient { BaseAddress = new Uri("http://localhost:19999") },
            preEndpoint:  "/handlers/h/pre",
            postEndpoint: "/handlers/h/post",
            failureMode:  "log");

        var binding = new HandlerBinding(
            HandlerId: $"{extensionId}-in",
            Seam: ExtensionSeams.AgentInput,
            Priority: priority,
            FailureMode: "log",
            HandlerInstance: new AgentInputHandlerProxy(proxy));

        return Task.FromResult(new ExtensionDescriptor(
            ExtensionId: extensionId,
            Version: "1.0.0",
            Manifest: manifest,
            Handlers: new[] { binding },
            LoadContext: null));
    }

    // ── Container-specific invocation tests ───────────────────────────────

    // CC-1. Proxy calls next() when container returns "next" action
    [Fact]
    public async Task ContainerProxy_NextAction_CallsNext()
    {
        using var server = new MockContainerServer(preAction: "next");
        var proxy  = MakeProxy(server, "/handlers/h-in/pre", "/handlers/h-in/post");
        var mw     = new AgentInputHandlerProxy(proxy);

        bool nextCalled = false;
        var ctx = new AgentInputContext { AgentId = "agent-a", RunId = "r1", Message = "hello" };
        await mw.InvokeAsync(ctx, () => { nextCalled = true; return Task.CompletedTask; });

        nextCalled.Should().BeTrue("'next' action must allow the chain to continue");
    }

    // CC-2. Proxy suppresses next() when container returns "shortCircuit" action
    [Fact]
    public async Task ContainerProxy_ShortCircuitAction_SuppressesNext()
    {
        using var server = new MockContainerServer(preAction: "shortCircuit");
        var proxy  = MakeProxy(server, "/handlers/h-in/pre", "/handlers/h-in/post");
        var mw     = new AgentInputHandlerProxy(proxy);

        bool nextCalled = false;
        var ctx = new AgentInputContext { AgentId = "agent-a", RunId = "r1", Message = "hello" };
        await mw.InvokeAsync(ctx, () => { nextCalled = true; return Task.CompletedTask; });

        nextCalled.Should().BeFalse("'shortCircuit' action must prevent next() from being called");
    }

    // CC-3. Proxy applies contextPatch to Properties when container returns "mutate" action
    [Fact]
    public async Task ContainerProxy_MutateAction_AppliesContextPatch()
    {
        var patch = new Dictionary<string, object?> { ["result"] = "from-container" };
        using var server = new MockContainerServer(preAction: "mutate", preContextPatch: patch);
        var proxy  = MakeProxy(server, "/handlers/h-in/pre", "/handlers/h-in/post");
        var mw     = new AgentInputHandlerProxy(proxy);

        var ctx = new AgentInputContext { AgentId = "agent-a", RunId = "r1", Message = "msg" };
        await mw.InvokeAsync(ctx, () => Task.CompletedTask);

        // contextPatch values arrive as JsonElement after HTTP round-trip; convert to string for comparison.
        ctx.Properties.Should().ContainKey("result");
        var value = ctx.Properties["result"]?.ToString();
        value.Should().Be("from-container",
            "contextPatch string values must be merge-patched into AgentInputContext.Properties");
    }

    // CC-4. Proxy applies failureMode=skip when container is unreachable
    [Fact]
    public async Task ContainerProxy_Unreachable_SkipMode_CallsNext()
    {
        var proxy = new HttpContainerHandlerProxy(
            new HttpClient { BaseAddress = new Uri("http://localhost:19999") },
            preEndpoint:  "/handlers/h-in/pre",
            postEndpoint: "/handlers/h-in/post",
            failureMode:  "skip");
        var mw = new AgentInputHandlerProxy(proxy);

        bool nextCalled = false;
        var ctx = new AgentInputContext { AgentId = "agent-a", RunId = "r1", Message = "msg" };
        await mw.InvokeAsync(ctx, () => { nextCalled = true; return Task.CompletedTask; });

        nextCalled.Should().BeTrue("failureMode=skip must call next() even when the container is unreachable");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static HttpContainerHandlerProxy MakeProxy(MockContainerServer server, string pre, string post) =>
        new(new HttpClient { BaseAddress = server.BaseUri }, pre, post, failureMode: "fail");

    // ── Mock container server ──────────────────────────────────────────────

    internal sealed class MockContainerServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly string _preAction;
        private readonly IReadOnlyDictionary<string, object?>? _preContextPatch;

        public Uri BaseUri { get; }

        public MockContainerServer(
            string preAction = "next",
            IReadOnlyDictionary<string, object?>? preContextPatch = null)
        {
            _preAction = preAction;
            _preContextPatch = preContextPatch;

            var port = FindFreePort();
            BaseUri = new Uri($"http://localhost:{port}");
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{port}/");
            _listener.Start();
            _ = ServeAsync(_cts.Token);
        }

        private async Task ServeAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { break; }
                _ = HandleAsync(ctx);
            }
        }

        private async Task HandleAsync(HttpListenerContext ctx)
        {
            ctx.Response.ContentType = "application/json";
            var path = ctx.Request.Url?.AbsolutePath ?? "";

            if (path.EndsWith("/pre", StringComparison.OrdinalIgnoreCase))
            {
                var body = JsonSerializer.Serialize(new
                {
                    action            = _preAction,
                    continuationToken = (string?)null,
                    contextPatch      = _preContextPatch,
                });
                await ctx.Response.OutputStream.WriteAsync(
                    System.Text.Encoding.UTF8.GetBytes(body));
            }
            else if (path.EndsWith("/post", StringComparison.OrdinalIgnoreCase))
            {
                var body = JsonSerializer.Serialize(new { action = "passThrough" });
                await ctx.Response.OutputStream.WriteAsync(
                    System.Text.Encoding.UTF8.GetBytes(body));
            }
            else
            {
                ctx.Response.StatusCode = 404;
            }

            ctx.Response.Close();
        }

        private static int FindFreePort()
        {
            using var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
        }
    }
}
