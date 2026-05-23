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
            failureMode:  "log",
            descriptor: new HandlerBindingDescriptor(extensionId, "1.0.0", $"{extensionId}-in", ExtensionSeams.AgentInput, "container"));

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
            failureMode:  "skip",
            descriptor: new HandlerBindingDescriptor("test-ext", "1.0.0", "h-in", ExtensionSeams.AgentInput, "container"));
        var mw = new AgentInputHandlerProxy(proxy);

        bool nextCalled = false;
        var ctx = new AgentInputContext { AgentId = "agent-a", RunId = "r1", Message = "msg" };
        await mw.InvokeAsync(ctx, () => { nextCalled = true; return Task.CompletedTask; });

        nextCalled.Should().BeTrue("failureMode=skip must call next() even when the container is unreachable");
    }

    // ── Tool gateway seam: /pre + /post round-trip over HTTP (impl plan :822) ─

    // CT-1. Round-trip: pre 'next' dispatches the tool, post observes, outcome passes through.
    [Fact]
    public async Task ToolProxy_NextAction_RoundTripsAndPassesOutcomeThrough()
    {
        using var server = new MockContainerServer(preAction: "next", postAction: "next");
        var mw = new ToolGatewayHandlerProxy(MakeToolProxy(server));

        var nextCalled = false;
        var outcome = await mw.InvokeAsync(ToolCtx(), () =>
        {
            nextCalled = true;
            return Task.FromResult(new ToolCallOutcome("call-1", "real-result", null));
        });

        nextCalled.Should().BeTrue("'next' must dispatch the tool");
        outcome.Result.Should().Be("real-result", "the dispatched tool's outcome passes through unchanged");
        outcome.Error.Should().BeNull();
    }

    // CT-2. shortCircuit deny: pre 'shortCircuit' returns the handler's outcome; tool never dispatched.
    [Fact]
    public async Task ToolProxy_ShortCircuit_DeniesWithoutDispatch()
    {
        using var server = new MockContainerServer(preAction: "shortCircuit", preError: "denied-by-policy");
        var mw = new ToolGatewayHandlerProxy(MakeToolProxy(server));

        var nextCalled = false;
        var outcome = await mw.InvokeAsync(ToolCtx(), () =>
        {
            nextCalled = true;
            return Task.FromResult(new ToolCallOutcome("call-1", "real-result", null));
        });

        nextCalled.Should().BeFalse("shortCircuit must prevent tool dispatch");
        outcome.Error.Should().Be("denied-by-policy");
        outcome.Result.Should().BeNull();
    }

    // CT-3. post mutate: tool dispatched, post replaces the outcome (e.g. redact result).
    [Fact]
    public async Task ToolProxy_PostMutate_ReplacesOutcome()
    {
        using var server = new MockContainerServer(
            preAction: "next", postAction: "mutate", postResult: "[redacted]");
        var mw = new ToolGatewayHandlerProxy(MakeToolProxy(server));

        var nextCalled = false;
        var outcome = await mw.InvokeAsync(ToolCtx(), () =>
        {
            nextCalled = true;
            return Task.FromResult(new ToolCallOutcome("call-1", "secret-result", null));
        });

        nextCalled.Should().BeTrue("'next' dispatches the tool before the post transform");
        outcome.Result.Should().Be("[redacted]", "post 'mutate' must replace the tool outcome");
    }

    // CT-4. failureMode=skip + unreachable container: tool is dispatched as if the handler were absent.
    [Fact]
    public async Task ToolProxy_Unreachable_SkipMode_DispatchesTool()
    {
        var proxy = new HttpContainerHandlerProxy(
            new HttpClient { BaseAddress = new Uri("http://localhost:19999") },
            preEndpoint:  "/handlers/h-tool/pre",
            postEndpoint: "/handlers/h-tool/post",
            failureMode:  "skip",
            descriptor: new HandlerBindingDescriptor("test-ext", "1.0.0", "h-tool", ExtensionSeams.ToolGatewayMiddleware, "container"));
        var mw = new ToolGatewayHandlerProxy(proxy);

        var nextCalled = false;
        var outcome = await mw.InvokeAsync(ToolCtx(), () =>
        {
            nextCalled = true;
            return Task.FromResult(new ToolCallOutcome("call-1", "real-result", null));
        });

        nextCalled.Should().BeTrue("failureMode=skip must dispatch the tool when the container is unreachable");
        outcome.Result.Should().Be("real-result");
    }

    // ── LLM gateway seam: full request/response /pre + /post round-trip over HTTP ─

    // CL-1. Round-trip: pre 'next' calls the model, post observes, response passes through.
    [Fact]
    public async Task LlmProxy_NextAction_RoundTripsAndPassesResponseThrough()
    {
        using var server = new MockContainerServer(preAction: "next", postAction: "next");
        var mw = new LlmGatewayHandlerProxy(MakeLlmProxy(server));

        var modelCalled = false;
        var resp = await ((IAgentFilter)mw).InvokeAsync(LlmReq(),
            (r, c) => { modelCalled = true; return Task.FromResult(new CompletionResponse("real-answer")); },
            CancellationToken.None);

        modelCalled.Should().BeTrue("'next' must call the model");
        resp.Text.Should().Be("real-answer");
    }

    // CL-2. shortCircuit: pre returns a synthetic response; the model is never called.
    [Fact]
    public async Task LlmProxy_ShortCircuit_ReturnsSyntheticResponseWithoutModel()
    {
        using var server = new MockContainerServer(preAction: "shortCircuit", preResponseText: "synthetic");
        var mw = new LlmGatewayHandlerProxy(MakeLlmProxy(server));

        var modelCalled = false;
        var resp = await ((IAgentFilter)mw).InvokeAsync(LlmReq(),
            (r, c) => { modelCalled = true; return Task.FromResult(new CompletionResponse("real-answer")); },
            CancellationToken.None);

        modelCalled.Should().BeFalse("shortCircuit must skip the model");
        resp.Text.Should().Be("synthetic");
    }

    // CL-3. post mutate: model called, post replaces the response (e.g. redact).
    [Fact]
    public async Task LlmProxy_PostMutate_ReplacesResponse()
    {
        using var server = new MockContainerServer(preAction: "next", postAction: "mutate", postResponseText: "[redacted]");
        var mw = new LlmGatewayHandlerProxy(MakeLlmProxy(server));

        var resp = await ((IAgentFilter)mw).InvokeAsync(LlmReq(),
            (r, c) => Task.FromResult(new CompletionResponse("secret-answer")),
            CancellationToken.None);

        resp.Text.Should().Be("[redacted]", "post 'mutate' must replace the model response");
    }

    // CL-4. Streaming bypasses the container LLM handler (pre/post can't express a stream).
    [Fact]
    public async Task LlmProxy_Streaming_PassesThroughWithoutContainer()
    {
        using var server = new MockContainerServer(preAction: "shortCircuit", preResponseText: "should-not-apply");
        var mw = new LlmGatewayHandlerProxy(MakeLlmProxy(server));

        var modelCalled = false;
        var stream = ((IStreamingAgentFilter)mw).InvokeAsync(LlmReq(),
            (r, c) => { modelCalled = true; return EmptyUpdates(); },
            CancellationToken.None);
        await foreach (var _ in stream) { }

        modelCalled.Should().BeTrue("streaming bypasses the container LLM handler and calls the model directly");
    }

    // ── errorInterceptor seam: single /error call ─────────────────────────────

    // CE-1. The handler rewrites the surfaced message.
    [Fact]
    public async Task ErrorProxy_RewritesMessage()
    {
        using var server = new MockContainerServer(preMessage: "[ext] enriched");
        var mw = new ErrorInterceptorHandlerProxy(MakeErrorProxy(server));

        var outcome = await mw.OnErrorAsync(
            new ErrorContext("agent-a", "r1", NodeId: null, "InvalidOperationException", "boom"));

        outcome.Message.Should().Be("[ext] enriched");
    }

    // CE-2. An unreachable interceptor never masks the failure (observe-only).
    [Fact]
    public async Task ErrorProxy_Unreachable_ObservesOnly()
    {
        var proxy = new HttpContainerHandlerProxy(
            new HttpClient { BaseAddress = new Uri("http://localhost:19999") },
            preEndpoint:  "/handlers/h-err/error",
            postEndpoint: "/handlers/h-err/post",
            failureMode:  "fail",
            descriptor: new HandlerBindingDescriptor("test-ext", "1.0.0", "h-err", ExtensionSeams.ErrorInterceptor, "container"));
        var mw = new ErrorInterceptorHandlerProxy(proxy);

        var outcome = await mw.OnErrorAsync(
            new ErrorContext("agent-a", "r1", NodeId: null, "InvalidOperationException", "boom"));

        outcome.Message.Should().BeNull("an unreachable interceptor must never mask or replace the failure");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static HttpContainerHandlerProxy MakeErrorProxy(MockContainerServer server) =>
        new(new HttpClient { BaseAddress = server.BaseUri }, "/handlers/h-err/pre", "/handlers/h-err/post",
            failureMode: "fail",
            descriptor: new HandlerBindingDescriptor("test-ext", "1.0.0", "h-err", ExtensionSeams.ErrorInterceptor, "container"));

    private static HttpContainerHandlerProxy MakeLlmProxy(MockContainerServer server) =>
        new(new HttpClient { BaseAddress = server.BaseUri }, "/handlers/h-llm/pre", "/handlers/h-llm/post",
            failureMode: "fail",
            descriptor: new HandlerBindingDescriptor("test-ext", "1.0.0", "h-llm", ExtensionSeams.LlmGatewayMiddleware, "container"));

    private static CompletionRequest LlmReq() =>
        new(new[] { new ChatTurn(AgentChatRole.User, "hello") });

    private static async IAsyncEnumerable<CompletionUpdate> EmptyUpdates()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static HttpContainerHandlerProxy MakeProxy(MockContainerServer server, string pre, string post) =>
        new(new HttpClient { BaseAddress = server.BaseUri }, pre, post, failureMode: "fail",
            descriptor: new HandlerBindingDescriptor("test-ext", "1.0.0", "h", ExtensionSeams.AgentInput, "container"));

    private static HttpContainerHandlerProxy MakeToolProxy(MockContainerServer server) =>
        new(new HttpClient { BaseAddress = server.BaseUri }, "/handlers/h-tool/pre", "/handlers/h-tool/post",
            failureMode: "fail",
            descriptor: new HandlerBindingDescriptor("test-ext", "1.0.0", "h-tool", ExtensionSeams.ToolGatewayMiddleware, "container"));

    private static readonly JsonElement EmptyArgs = JsonDocument.Parse("{}").RootElement.Clone();

    private static ToolGatewayContext ToolCtx() =>
        new("some_tool", "call-1", EmptyArgs, new AgentContext(AgentName: "agent-a") { RunId = "r1" });

    // ── Mock container server ──────────────────────────────────────────────

    internal sealed class MockContainerServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly string _preAction;
        private readonly IReadOnlyDictionary<string, object?>? _preContextPatch;
        private readonly string? _preResult;
        private readonly string? _preError;
        private readonly string _postAction;
        private readonly string? _postResult;
        private readonly string? _postError;
        private readonly string? _preResponseText;
        private readonly string? _postResponseText;
        private readonly string? _preMessage;

        public Uri BaseUri { get; }

        public MockContainerServer(
            string preAction = "next",
            IReadOnlyDictionary<string, object?>? preContextPatch = null,
            string? preResult = null,
            string? preError = null,
            string postAction = "passThrough",
            string? postResult = null,
            string? postError = null,
            string? preResponseText = null,
            string? postResponseText = null,
            string? preMessage = null)
        {
            _preAction = preAction;
            _preContextPatch = preContextPatch;
            _preResult = preResult;
            _preError = preError;
            _postAction = postAction;
            _postResult = postResult;
            _postError = postError;
            _preResponseText = preResponseText;
            _postResponseText = postResponseText;
            _preMessage = preMessage;

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
                    result            = _preResult,
                    error             = _preError,
                    response          = _preResponseText is null ? null : new { text = _preResponseText },
                    message           = _preMessage,
                });
                await ctx.Response.OutputStream.WriteAsync(
                    System.Text.Encoding.UTF8.GetBytes(body));
            }
            else if (path.EndsWith("/post", StringComparison.OrdinalIgnoreCase))
            {
                var body = JsonSerializer.Serialize(new
                {
                    action   = _postAction,
                    result   = _postResult,
                    error    = _postError,
                    response = _postResponseText is null ? null : new { text = _postResponseText },
                });
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
