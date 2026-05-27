// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Vais.Agents.Core;
using Vais.Agents.Runtime.Instantiation;
using Xunit;

namespace Vais.Agents.Runtime.Plugins.Container.Tests;

/// <summary>
/// G6 — verify <see cref="ContainerAgentShim"/> runs the per-agent
/// <see cref="AgentInputMiddleware"/> chain on the raw user message before sending the invoke
/// request to the plugin (P12 §1, "runtime owns input shaping"). The chain is resolved via
/// <see cref="IAgentManifestTranslator.ResolvePerAgentChainsAsync"/> at invoke time. Test rigs
/// without a translator wired skip shaping silently (backwards-compat).
/// </summary>
public sealed class ContainerAgentShimInputMiddlewareTests
{
    private static readonly JsonSerializerOptions s_webOpts = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task AskAsync_WithInputMiddleware_ShapesMessageBeforePluginSeesIt()
    {
        var probe = new ProbeInputMiddleware();
        var translator = TranslatorReturning(new PerAgentChains(
            Llm: Array.Empty<LlmGatewayMiddleware>(),
            Tool: Array.Empty<ToolGatewayMiddleware>(),
            Input: new AgentInputMiddleware[] { probe },
            Budget: null));

        var (shim, handler) = MakeShim(translator);
        PluginInvokeRequest? captured = null;
        handler.InvokeHandler = async req =>
        {
            captured = await req.Content!.ReadFromJsonAsync<PluginInvokeRequest>(s_webOpts);
            return OkInvoke("done");
        };

        await shim.AskAsync("hello");

        probe.LastSeenContext.Should().NotBeNull(because: "the shim must invoke the input chain");
        probe.LastSeenContext!.AgentId.Should().Be("agent-1");
        probe.RawMessageObserved.Should().Be("hello", because: "the chain sees the raw user message first");

        captured.Should().NotBeNull();
        captured!.Messages.Should().HaveCount(1);
        captured.Messages[0].Content.Should().Be("[shaped] hello",
            because: "the plugin must receive the shaped message, not the raw one (P12 §1)");
    }

    [Fact]
    public async Task AskAsync_WithoutTranslator_SkipsShapingSilently()
    {
        // Backwards-compat path: test rigs that don't wire IAgentManifestTranslator continue
        // to work — the shim falls back to the raw user message and the plugin sees it unchanged.
        var (shim, handler) = MakeShim(translator: null);
        PluginInvokeRequest? captured = null;
        handler.InvokeHandler = async req =>
        {
            captured = await req.Content!.ReadFromJsonAsync<PluginInvokeRequest>(s_webOpts);
            return OkInvoke("done");
        };

        await shim.AskAsync("hello");

        captured!.Messages[0].Content.Should().Be("hello",
            because: "no translator → no shaping → plugin sees the raw message");
    }

    [Fact]
    public async Task AskAsync_WithEmptyInputChain_SkipsShapingSilently()
    {
        // When the manifest has no input middleware (most agents today), the chain is empty
        // and the shim short-circuits before allocating an AgentInputContext.
        var translator = TranslatorReturning(new PerAgentChains(
            Llm: Array.Empty<LlmGatewayMiddleware>(),
            Tool: Array.Empty<ToolGatewayMiddleware>(),
            Input: Array.Empty<AgentInputMiddleware>(),
            Budget: null));

        var (shim, handler) = MakeShim(translator);
        PluginInvokeRequest? captured = null;
        handler.InvokeHandler = async req =>
        {
            captured = await req.Content!.ReadFromJsonAsync<PluginInvokeRequest>(s_webOpts);
            return OkInvoke("done");
        };

        await shim.AskAsync("hello");

        captured!.Messages[0].Content.Should().Be("hello");
    }

    [Fact]
    public async Task AskAsync_WhenTranslatorThrows_FallsBackToRawMessage()
    {
        // Defensive: if the manifest is unreachable mid-call (translator threw), we must not
        // fail the invocation — the downstream gateway will surface the real error if there
        // is one. Skip shaping; send the raw message.
        var translator = Substitute.For<IAgentManifestTranslator>();
        translator.ResolvePerAgentChainsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<PerAgentChains>>(_ => throw new InvalidOperationException("registry borked"));

        var (shim, handler) = MakeShim(translator);
        PluginInvokeRequest? captured = null;
        handler.InvokeHandler = async req =>
        {
            captured = await req.Content!.ReadFromJsonAsync<PluginInvokeRequest>(s_webOpts);
            return OkInvoke("done");
        };

        await shim.AskAsync("hello");

        captured!.Messages[0].Content.Should().Be("hello");
    }

    [Fact]
    public async Task StreamAsync_WithInputMiddleware_ShapesMessageBeforePluginSeesIt()
    {
        var probe = new ProbeInputMiddleware();
        var translator = TranslatorReturning(new PerAgentChains(
            Llm: Array.Empty<LlmGatewayMiddleware>(),
            Tool: Array.Empty<ToolGatewayMiddleware>(),
            Input: new AgentInputMiddleware[] { probe },
            Budget: null));

        var (shim, handler) = MakeShim(translator);
        PluginInvokeRequest? captured = null;
        handler.StreamHandler = async req =>
        {
            captured = await req.Content!.ReadFromJsonAsync<PluginInvokeRequest>(s_webOpts);
            return SseStream(["done"]);
        };

        var ctx = new AgentContext(AgentName: "agent-1") { RunId = "run-stream" };
        var events = new List<AgentEvent>();
        await foreach (var ev in shim.StreamAsync("hello", ctx, CancellationToken.None))
            events.Add(ev);

        probe.LastSeenContext.Should().NotBeNull();
        probe.LastSeenContext!.RunId.Should().Be("run-stream",
            because: "StreamAsync passes its AgentContext.RunId into AgentInputContext");
        captured!.Messages[0].Content.Should().Be("[shaped] hello");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static IAgentManifestTranslator TranslatorReturning(PerAgentChains chains)
    {
        var t = Substitute.For<IAgentManifestTranslator>();
        t.ResolvePerAgentChainsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<PerAgentChains>(chains));
        return t;
    }

    private static HttpResponseMessage OkInvoke(string assistantMessage) =>
        new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { assistantMessage, opaqueState = (object?)null }, options: s_webOpts),
        };

    private static HttpResponseMessage SseStream(IReadOnlyList<string> deltas)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var d in deltas)
        {
            sb.Append("event: delta\n");
            sb.Append("data: ").Append(JsonSerializer.Serialize(new { text = d }, s_webOpts)).Append('\n').Append('\n');
        }
        sb.Append("event: done\n");
        sb.Append("data: ").Append(JsonSerializer.Serialize(
            new { assistantMessage = "done", opaqueState = (object?)null }, s_webOpts)).Append('\n').Append('\n');

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sb.ToString(), System.Text.Encoding.UTF8, "text/event-stream"),
        };
    }

    private static (ContainerAgentShim Shim, InputTestHttpHandler Handler) MakeShim(IAgentManifestTranslator? translator)
    {
        var handler = new InputTestHttpHandler();
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:8080"),
        };

        var manifest = new AgentManifest("agent-1", "1.0", new AgentHandlerRef("Test"), [], []);
        var tokenSvc = Substitute.For<ICallTokenService>();
        tokenSvc.Generate(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>()).Returns("test-token");
        tokenSvc.Generate(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<AgentContextClaims>(), Arg.Any<int>())
            .Returns("test-token");

        var shim = new ContainerAgentShim(
            supervisor: null!,
            invokeClient: httpClient,
            preprocessors: [],
            manifest: manifest,
            callTokenService: tokenSvc,
            internalLlmGatewayUrl: "http://gateway/llm",
            internalToolGatewayUrl: "http://gateway/tools",
            invokeTimeoutSeconds: 60,
            sessionConfig: null,
            invokeIdleTimeoutSeconds: null,
            contextAccessor: null,
            translator: translator,
            logger: NullLogger.Instance);

        return (shim, handler);
    }

    private sealed class ProbeInputMiddleware : AgentInputMiddleware
    {
        public AgentInputContext? LastSeenContext;
        public string? RawMessageObserved;

        public override Task InvokeAsync(AgentInputContext context, Func<Task> next, CancellationToken cancellationToken = default)
        {
            LastSeenContext = context;
            RawMessageObserved = context.Message;
            context.Message = $"[shaped] {context.Message}";
            return next();
        }
    }

    private sealed class InputTestHttpHandler : HttpMessageHandler
    {
        internal Func<HttpRequestMessage, Task<HttpResponseMessage>> InvokeHandler { get; set; }
            = _ => Task.FromResult(OkInvoke("default"));

        internal Func<HttpRequestMessage, Task<HttpResponseMessage>>? StreamHandler { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get &&
                request.RequestUri?.PathAndQuery is "/health" or "/health/")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

            var path = request.RequestUri?.PathAndQuery ?? "";
            if (path.EndsWith("/v1/stream", StringComparison.OrdinalIgnoreCase) && StreamHandler is not null)
                return StreamHandler(request);

            return InvokeHandler(request);
        }
    }
}
