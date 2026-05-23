// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using System.Net;
using System.Text.Json;
using Xunit;

namespace Vais.Agents.Runtime.Extensions.Container.Tests;

/// <summary>
/// Integration tests for <see cref="ContainerExtensionLifecycleManager"/> using a
/// local HTTP server that simulates a well-behaved container extension.
/// </summary>
public sealed class ContainerExtensionLifecycleTests : IDisposable
{
    // ── Helpers ────────────────────────────────────────────────────────────────

    private static ExtensionManifest MakeManifest(
        string id = "test-ext",
        string version = "1.0.0",
        string? image = null,
        int? port = null,
        params (string id, string seam)[] handlers)
    {
        var handlerList = handlers.Length > 0
            ? handlers.Select(h => new ExtensionHandler { Id = h.id, Seam = h.seam }).ToList()
            : new List<ExtensionHandler>
                {
                    new() { Id = "h-in",  Seam = ExtensionSeams.AgentInput  },
                    new() { Id = "h-out", Seam = ExtensionSeams.AgentOutput },
                };
        return new ExtensionManifest(
            Id: id,
            Version: version,
            Spec: new ExtensionSpec
            {
                Host       = "container",
                Image      = image,
                Port       = port,
                Handlers   = handlerList,
            });
    }

    private static (ContainerExtensionLifecycleManager manager, ExtensionHandlerRegistry registry, StubContainerHost host)
        MakeManager(Uri? baseUri = null)
    {
        var registry = new ExtensionHandlerRegistry();
        var composer = new DefaultExtensionChainComposer(registry, agentRegistry: null);
        var host = new StubContainerHost(baseUri);
        var manager = new ContainerExtensionLifecycleManager(registry, composer, host);
        return (manager, registry, host);
    }

    // ── 1. Discovery cross-check: handler mismatch returns failure ─────────────
    [Fact]
    public async Task Apply_HandlerMismatch_ReturnsFailure()
    {
        using var server = new MockContainerServer(advertised: new[] { ("h-in", "agentInput") });
        var (manager, registry, _) = MakeManager(server.BaseUri);

        // Manifest declares h-in + h-out; container only advertises h-in
        var manifest = MakeManifest(port: server.Port);
        var result = await manager.ApplyAsync(manifest);

        result.Status.Should().Be(ExtensionReloadStatus.LoadFailed);
        result.FailureUrn.Should().Be(ExtensionUrns.HandlerMismatch);
        registry.Snapshot().Should().BeEmpty();
    }

    // ── 2. Successful apply: handlers registered in registry ──────────────────
    [Fact]
    public async Task Apply_Success_RegistersHandlers()
    {
        using var server = new MockContainerServer(advertised: new[]
        {
            ("h-in",  "agentInput"),
            ("h-out", "agentOutput"),
        });
        var (manager, registry, _) = MakeManager(server.BaseUri);
        var manifest = MakeManifest(port: server.Port);

        var result = await manager.ApplyAsync(manifest);

        result.Status.Should().Be(ExtensionReloadStatus.Success);
        result.NewDescriptor.Should().NotBeNull();
        result.NewDescriptor!.Handlers.Should().HaveCount(2);
        registry.Snapshot().Should().ContainKey("test-ext");
    }

    // ── 3. Remove: clears handler from registry ────────────────────────────────
    [Fact]
    public async Task Remove_AfterApply_ClearsRegistry()
    {
        using var server = new MockContainerServer(advertised: new[]
        {
            ("h-in",  "agentInput"),
            ("h-out", "agentOutput"),
        });
        var (manager, registry, _) = MakeManager(server.BaseUri);
        var manifest = MakeManifest(port: server.Port);

        await manager.ApplyAsync(manifest);
        var unload = await manager.RemoveAsync("test-ext");

        unload.Status.Should().Be(ExtensionUnloadStatus.Success);
        registry.Snapshot().Should().BeEmpty();
    }

    // ── 4. Remove non-existent: NotFound ──────────────────────────────────────
    [Fact]
    public async Task Remove_NotFound_ReturnsNotFound()
    {
        var (manager, _, _) = MakeManager();
        var result = await manager.RemoveAsync("no-such-ext");
        result.Status.Should().Be(ExtensionUnloadStatus.NotFound);
    }

    // ── 5. HotSeamGuard: empty hot-seam set — no violations ───────────────────
    [Fact]
    public void HotSeamGuard_EmptyHotSeams_NoViolations()
    {
        var guard = HotSeamGuard.Default;
        var manifest = MakeManifest();
        guard.Evaluate(manifest).Should().BeEmpty();
    }

    // ── 6. HotSeamGuard: configured hot seam — violation returned ─────────────
    [Fact]
    public void HotSeamGuard_ConfiguredHotSeam_ReturnsViolation()
    {
        var guard = new HotSeamGuard(new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "agentInput" });

        var manifest = new ExtensionManifest(
            Id: "test",
            Version: "1.0",
            Spec: new ExtensionSpec
            {
                Host = "container",
                Handlers = new List<ExtensionHandler>
                {
                    new() { Id = "h1", Seam = "agentInput" },
                    new() { Id = "h2", Seam = "agentOutput" },
                },
            });

        var violations = guard.Evaluate(manifest);
        violations.Should().ContainSingle(v => v.HandlerId == "h1" && v.Seam == "agentInput");
    }

    // ── 6b. HotSeamGuard.Default flags an llmGatewayMiddleware container handler ─
    [Fact]
    public void HotSeamGuard_Default_FlagsLlmContainerSeam()
    {
        var manifest = MakeManifest(handlers: new[] { ("h-llm", ExtensionSeams.LlmGatewayMiddleware) });

        var violations = HotSeamGuard.Default.Evaluate(manifest);

        violations.Should().ContainSingle(v => v.HandlerId == "h-llm" && v.Seam == ExtensionSeams.LlmGatewayMiddleware);
    }

    // ── 7. Proxy shortCircuit: chain does not call next ───────────────────────
    [Fact]
    public async Task AgentInputProxy_ShortCircuit_DoesNotCallNext()
    {
        using var server = new MockContainerServer(
            advertised: new[] { ("h-in", "agentInput") },
            preAction: "shortCircuit");
        var (manager, registry, _) = MakeManager(server.BaseUri);
        var manifest = MakeManifest(port: server.Port,
            handlers: new[] { ("h-in", "agentInput") });

        await manager.ApplyAsync(manifest);

        var descriptor = registry.Snapshot()["test-ext"];
        var proxy = descriptor.Handlers[0].HandlerInstance as AgentInputMiddleware;
        proxy.Should().NotBeNull();

        bool nextCalled = false;
        var ctx = new AgentInputContext { AgentId = "a", RunId = "r", Message = "hello" };
        await proxy!.InvokeAsync(ctx, () => { nextCalled = true; return Task.CompletedTask; });

        nextCalled.Should().BeFalse("shortCircuit suppresses next()");
    }

    // ── 8. GET /v1/handlers failure: discovery failed ─────────────────────────
    [Fact]
    public async Task Apply_ContainerUnreachable_ReturnsDiscoveryFailed()
    {
        var (manager, _, _) = MakeManager(new Uri("http://localhost:19999")); // nothing listening
        var manifest = MakeManifest(port: 19999);

        var result = await manager.ApplyAsync(manifest);

        result.Status.Should().Be(ExtensionReloadStatus.LoadFailed);
        result.FailureUrn.Should().Be(ExtensionUrns.HandlerDiscoveryFailed);
    }

    // ── Test doubles ───────────────────────────────────────────────────────────

    private sealed class StubContainerHost(Uri? fixedBaseUri) : IContainerExtensionHost
    {
        public ValueTask<Uri?> StartAsync(ExtensionManifest manifest, CancellationToken ct = default)
            => new(fixedBaseUri);

        public ValueTask StopAsync(string extensionId, CancellationToken ct = default)
            => ValueTask.CompletedTask;
    }

    private sealed class MockContainerServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly (string id, string seam)[] _handlers;
        private readonly string _preAction;

        public Uri BaseUri { get; }
        public int Port { get; }

        public MockContainerServer(
            (string id, string seam)[] advertised,
            string preAction = "next")
        {
            _handlers = advertised;
            _preAction = preAction;

            // Find a free port
            Port = FindFreePort();
            BaseUri = new Uri($"http://localhost:{Port}");
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{Port}/");
            _listener.Start();
            _ = ServeAsync(_cts.Token);
        }

        private async Task ServeAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch
                {
                    break;
                }

                _ = HandleAsync(ctx);
            }
        }

        private async Task HandleAsync(HttpListenerContext ctx)
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "";
            ctx.Response.ContentType = "application/json";

            if (path == "/v1/handlers" && ctx.Request.HttpMethod == "GET")
            {
                var body = JsonSerializer.Serialize(new
                {
                    extensionId = "test-ext",
                    version = "1.0.0",
                    targetApiVersion = "0.30",
                    handlers = _handlers.Select(h => new
                    {
                        id = h.id,
                        seam = h.seam,
                        preEndpoint  = $"/handlers/{h.id}/pre",
                        postEndpoint = $"/handlers/{h.id}/post",
                    }).ToArray(),
                });
                var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                await ctx.Response.OutputStream.WriteAsync(bytes);
            }
            else if (path.EndsWith("/pre") && ctx.Request.HttpMethod == "POST")
            {
                var body = JsonSerializer.Serialize(new
                {
                    action = _preAction,
                    continuationToken = (string?)null,
                    contextPatch = (object?)null,
                });
                var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                await ctx.Response.OutputStream.WriteAsync(bytes);
            }
            else if (path.EndsWith("/post") && ctx.Request.HttpMethod == "POST")
            {
                var body = JsonSerializer.Serialize(new { action = "passThrough" });
                var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                await ctx.Response.OutputStream.WriteAsync(bytes);
            }
            else
            {
                ctx.Response.StatusCode = 404;
            }

            ctx.Response.Close();
        }

        private static int FindFreePort()
        {
            using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
        }
    }

    public void Dispose() { }
}
