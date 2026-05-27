// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Vais.Agents.Core;
using Xunit;

namespace Vais.Agents.Runtime.Plugins.Container.Tests;

/// <summary>
/// Unit tests for <see cref="ContainerAgentShim"/>. A <see cref="FakeHttpHandler"/>
/// drives the HTTP boundary so no real container or Docker daemon is needed.
/// </summary>
public sealed class ContainerAgentShimTests
{
    // ── JSON helpers (match ContainerJsonOptions.Default = web/camelCase) ──

    private static readonly JsonSerializerOptions s_webOpts = new(JsonSerializerDefaults.Web);

    private static HttpResponseMessage OkInvoke(
        string assistantMessage,
        object? opaqueState = null) =>
        new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(
                new { assistantMessage, opaqueState },
                options: s_webOpts)
        };

    private static HttpResponseMessage ErrorResponse(
        HttpStatusCode status, string errorType, string errorMessage = "test error") =>
        new(status)
        {
            Content = JsonContent.Create(
                new { errorType, errorMessage },
                options: s_webOpts)
        };

    // ── Streaming idle / absolute watchdog (IT-3) ──────────────────────────

    [Fact]
    public async Task StreamAsync_IdleTimeout_AbortsWithTurnFailed()
    {
        // Container sends headers, then goes silent forever. Idle watchdog (1s) must trip.
        var (shim, handler) = MakeShim(invokeIdleTimeoutSeconds: 1);
        handler.InvokeHandler = _ => Task.FromResult(SseResponse((System.Threading.Timeout.Infinite, "")));

        var events = await CollectAsync(shim);

        events.Should().Contain(e => e is TurnFailed);
        events.Should().NotContain(e => e is TurnCompleted);
        events.OfType<TurnFailed>().Single().ErrorMessage.Should().Contain("idle");
    }

    [Fact]
    public async Task StreamAsync_SseHeartbeats_KeepInvokeAlive_ThenCompletes()
    {
        // Heartbeat comments arrive every 300ms (< the 1s idle window), so the invoke is NOT idle and
        // runs to a normal completion. Proves the heartbeat ':' line resets the idle deadline.
        var (shim, handler) = MakeShim(invokeIdleTimeoutSeconds: 1);
        handler.InvokeHandler = _ => Task.FromResult(SseResponse(
            (300, ": heartbeat\n\n"),
            (300, ": heartbeat\n\n"),
            (300, ": heartbeat\n\n"),
            (300, "event: done\ndata: {\"assistantMessage\":\"ok\"}\n\n")));

        var events = await CollectAsync(shim);

        events.Should().NotContain(e => e is TurnFailed);
        events.OfType<TurnCompleted>().Single().AssistantText.Should().Be("ok");
    }

    [Fact]
    public async Task StreamAsync_MaxDuration_AbortsEvenWhileHeartbeating()
    {
        // Session mode with a 1s absolute cap. The container keeps heartbeating (never idle), but the
        // hard ceiling must still abort it — a non-idle runaway is bounded by sessionTtlSeconds.
        var (shim, handler) = MakeShim(invokeIdleTimeoutSeconds: 5, sessionConfig: SessionConfig(sessionTtlSeconds: 1));
        handler.InvokeHandler = _ => Task.FromResult(SseResponse(
            Enumerable.Range(0, 100).Select(_ => (200, ": heartbeat\n\n")).ToArray()));

        var events = await CollectAsync(shim);

        events.Should().Contain(e => e is TurnFailed);
        events.OfType<TurnFailed>().Single().ErrorMessage.Should().Contain("maximum duration");
    }

    private static async Task<List<AgentEvent>> CollectAsync(ContainerAgentShim shim)
    {
        var events = new List<AgentEvent>();
        await foreach (var ev in shim.StreamAsync("hi", AgentContext.Empty, CancellationToken.None))
            events.Add(ev);
        return events;
    }

    /// <summary>Builds a 200 text/event-stream response whose body emits each (delayMs, text) chunk in turn.</summary>
    private static HttpResponseMessage SseResponse(params (int DelayMs, string Text)[] chunks)
    {
        var content = new StreamContent(new DelayedChunkStream(chunks));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    /// <summary>Read-only stream that yields scripted chunks, waiting DelayMs (honoring ct) before each.</summary>
    private sealed class DelayedChunkStream(IEnumerable<(int DelayMs, string Text)> chunks) : Stream
    {
        private readonly IEnumerator<(int DelayMs, string Text)> _chunks = chunks.GetEnumerator();
        private byte[] _current = [];
        private int _pos;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_pos >= _current.Length)
            {
                if (!_chunks.MoveNext()) return 0; // EOF
                var (delayMs, text) = _chunks.Current;
                await Task.Delay(
                    delayMs == System.Threading.Timeout.Infinite
                        ? System.Threading.Timeout.InfiniteTimeSpan
                        : TimeSpan.FromMilliseconds(delayMs),
                    ct).ConfigureAwait(false);
                _current = System.Text.Encoding.UTF8.GetBytes(text);
                _pos = 0;
                if (_current.Length == 0) return 0;
            }
            var n = Math.Min(buffer.Length, _current.Length - _pos);
            _current.AsSpan(_pos, n).CopyTo(buffer.Span);
            _pos += n;
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ── Fake HTTP handler ──────────────────────────────────────────────────

    /// <summary>
    /// Routes GET /health → 200. All other requests are forwarded to <see cref="InvokeHandler"/>.
    /// </summary>
    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        internal Func<HttpRequestMessage, Task<HttpResponseMessage>> InvokeHandler { get; set; }
            = _ => Task.FromResult(OkInvoke("default reply"));

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get &&
                request.RequestUri?.PathAndQuery is "/health" or "/health/")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

            return InvokeHandler(request);
        }
    }

    // ── Factory helpers ────────────────────────────────────────────────────

    private static AgentManifest MakeManifest(string agentId = "test-agent") =>
        new(agentId, "1.0", new AgentHandlerRef("Test"), [], []);

    private static (ContainerAgentShim Shim, FakeHttpHandler Handler) MakeShim(
        string agentId = "test-agent",
        int? invokeIdleTimeoutSeconds = null,
        ContainerSessionTokenConfig? sessionConfig = null)
    {
        var handler = new FakeHttpHandler();
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:8080"),
        };

        var tokenSvc = Substitute.For<ICallTokenService>();
        tokenSvc.Generate(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>())
                .Returns("test-call-token");
        tokenSvc.Generate(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>())
                .Returns("test-call-token");

        var shim = new ContainerAgentShim(
            supervisor: null!,
            invokeClient: httpClient,
            preprocessors: [],
            manifest: MakeManifest(agentId),
            callTokenService: tokenSvc,
            internalLlmGatewayUrl: "http://gateway/llm",
            internalToolGatewayUrl: "http://gateway/tools",
            invokeTimeoutSeconds: 60,
            sessionConfig: sessionConfig,
            invokeIdleTimeoutSeconds: invokeIdleTimeoutSeconds,
            contextAccessor: null,
            logger: NullLogger.Instance);

        return (shim, handler);
    }

    private static ContainerSessionTokenConfig SessionConfig(int sessionTtlSeconds) =>
        new(sessionTtlSeconds, RenewTokenTtlSeconds: 120,
            RenewTokenUrl: "http://gateway/renew", LeaseStore: new InMemoryInvokeLeaseStore());

    // ── AskAsync — basic path ──────────────────────────────────────────────

    [Fact]
    public async Task AskAsync_ValidResponse_ReturnsAssistantMessage()
    {
        var (shim, handler) = MakeShim();
        handler.InvokeHandler = _ => Task.FromResult(OkInvoke("Hello from container!"));

        var reply = await shim.AskAsync("Hi there");

        reply.Should().Be("Hello from container!");
    }

    [Fact]
    public async Task AskAsync_ValidResponse_AppendsBothTurnsToHistory()
    {
        var (shim, handler) = MakeShim();
        handler.InvokeHandler = _ => Task.FromResult(OkInvoke("Assistant reply"));

        await shim.AskAsync("User message");

        shim.History.Should().HaveCount(2);
        shim.History[0].Role.Should().Be(AgentChatRole.User);
        shim.History[0].Text.Should().Be("User message");
        shim.History[1].Role.Should().Be(AgentChatRole.Assistant);
        shim.History[1].Text.Should().Be("Assistant reply");
    }

    [Fact]
    public async Task AskAsync_MultipleTurns_AccumulatesHistory()
    {
        var (shim, handler) = MakeShim();
        handler.InvokeHandler = _ => Task.FromResult(OkInvoke("reply"));

        await shim.AskAsync("turn one");
        await shim.AskAsync("turn two");

        shim.History.Should().HaveCount(4);
    }

    // ── AskAsync — opaque state round-trip ────────────────────────────────

    [Fact]
    public async Task AskAsync_OpaqueState_NotSentOnFirstCall()
    {
        var (shim, handler) = MakeShim();
        PluginInvokeRequest? captured = null;

        handler.InvokeHandler = async req =>
        {
            captured = await req.Content!.ReadFromJsonAsync<PluginInvokeRequest>(s_webOpts);
            return OkInvoke("reply", new { key = "val" });
        };

        await shim.AskAsync("first call");

        captured!.OpaqueState.Should().BeNull("first call has no prior opaque state");
    }

    [Fact]
    public async Task AskAsync_OpaqueState_SentOnSubsequentCall()
    {
        var (shim, handler) = MakeShim();
        var capturedStates = new List<JsonElement?>();
        var callCount = 0;

        handler.InvokeHandler = async req =>
        {
            var body = await req.Content!.ReadFromJsonAsync<PluginInvokeRequest>(s_webOpts);
            capturedStates.Add(body!.OpaqueState);
            return callCount++ == 0
                ? OkInvoke("reply-1", new { key = "val" })
                : OkInvoke("reply-2");
        };

        await shim.AskAsync("turn 1");
        await shim.AskAsync("turn 2");

        capturedStates.Should().HaveCount(2);
        capturedStates[0].Should().BeNull("first call has no prior state");
        capturedStates[1].Should().NotBeNull("second call carries state from first response");
        capturedStates[1]!.Value.GetProperty("key").GetString().Should().Be("val");
    }

    // ── AskAsync — 422 fresh-start retry ──────────────────────────────────

    [Fact]
    public async Task AskAsync_422OpaqueStateDeserializationError_ClearsStateAndRetries()
    {
        var (shim, handler) = MakeShim();

        // Prime shim with opaque state by doing a first successful call.
        handler.InvokeHandler = _ => Task.FromResult(OkInvoke("primed", new { x = 1 }));
        await shim.AskAsync("prime");

        // Now the container rejects the opaque state on first attempt, succeeds on retry.
        var attemptCount = 0;
        handler.InvokeHandler = req =>
        {
            attemptCount++;
            if (attemptCount == 1)
                return Task.FromResult(ErrorResponse(
                    HttpStatusCode.UnprocessableContent,
                    "OpaqueStateDeserializationError"));
            return Task.FromResult(OkInvoke("fresh start reply"));
        };

        var reply = await shim.AskAsync("trigger retry");

        reply.Should().Be("fresh start reply");
        attemptCount.Should().Be(2);
    }

    [Fact]
    public async Task AskAsync_422OpaqueStateDeserializationError_BothAttemptsFail_Throws()
    {
        var (shim, handler) = MakeShim();

        handler.InvokeHandler = _ => Task.FromResult(ErrorResponse(
            HttpStatusCode.UnprocessableContent,
            "OpaqueStateDeserializationError"));

        var act = () => shim.AskAsync("trigger double failure");
        await act.Should().ThrowAsync<OpaqueStateDeserializationException>();
    }

    // ── SetGrainState ──────────────────────────────────────────────────────

    [Fact]
    public void SetGrainState_SeedsHistoryFromGrainState()
    {
        var (shim, handler) = MakeShim();
        handler.InvokeHandler = _ => Task.FromResult(OkInvoke("reply"));

        var grainState = new FakeGrainState
        {
            HistoryItems =
            [
                new ChatTurn(AgentChatRole.User, "seeded user turn"),
                new ChatTurn(AgentChatRole.Assistant, "seeded assistant turn"),
            ],
            OpaqueStateJson = null,
        };

        ((IAgentGrainStateConsumer)shim).SetGrainState(grainState);

        shim.History.Should().HaveCount(2);
        shim.History[0].Text.Should().Be("seeded user turn");
        shim.History[1].Text.Should().Be("seeded assistant turn");
    }

    [Fact]
    public void SetGrainState_SeedsOpaqueStateFromGrainState()
    {
        var (shim, _) = MakeShim();
        var grainState = new FakeGrainState { OpaqueStateJson = """{"persisted":"blob"}""" };

        ((IAgentGrainStateConsumer)shim).SetGrainState(grainState);

        ((IOpaqueStateCarrier)shim).OpaqueState.Should().Be("""{"persisted":"blob"}""");
    }

    // ── Reset ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reset_ClearsHistoryAndOpaqueState()
    {
        var (shim, handler) = MakeShim();
        handler.InvokeHandler = _ => Task.FromResult(OkInvoke("reply", new { s = 1 }));

        await shim.AskAsync("before reset");
        shim.History.Should().HaveCount(2);
        ((IOpaqueStateCarrier)shim).OpaqueState.Should().NotBeNull();

        shim.Reset();

        shim.History.Should().BeEmpty();
        ((IOpaqueStateCarrier)shim).OpaqueState.Should().BeNull();
    }

    // ── IOpaqueStateCarrier ────────────────────────────────────────────────

    [Fact]
    public async Task IOpaqueStateCarrier_AfterAsk_ExposesNewOpaqueState()
    {
        var (shim, handler) = MakeShim();
        handler.InvokeHandler = _ => Task.FromResult(OkInvoke("reply", new { token = "abc" }));

        await shim.AskAsync("hi");

        var raw = ((IOpaqueStateCarrier)shim).OpaqueState;
        raw.Should().NotBeNullOrEmpty();
        using var doc = JsonDocument.Parse(raw!);
        doc.RootElement.GetProperty("token").GetString().Should().Be("abc");
    }

    [Fact]
    public async Task IOpaqueStateCarrier_AfterReset_ExposesNull()
    {
        var (shim, handler) = MakeShim();
        handler.InvokeHandler = _ => Task.FromResult(OkInvoke("reply", new { token = "abc" }));

        await shim.AskAsync("hi");
        shim.Reset();

        ((IOpaqueStateCarrier)shim).OpaqueState.Should().BeNull();
    }

    // ── IAgentSession / SystemPrompt ──────────────────────────────────────

    [Fact]
    public void SystemPrompt_CanBeSetAndRead()
    {
        var (shim, _) = MakeShim();
        shim.SystemPrompt.Should().BeNull();
        shim.SystemPrompt = "You are a test assistant.";
        shim.SystemPrompt.Should().Be("You are a test assistant.");
    }

    [Fact]
    public void Session_AgentIdMatchesManifestId()
    {
        var (shim, _) = MakeShim("my-container-agent");
        shim.Session.Should().NotBeNull();
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private sealed class FakeGrainState : IAgentGrainStateView
    {
        public string? SystemPrompt => null;
        public IReadOnlyList<ChatTurn> History => HistoryItems;
        public string? OpaqueState => OpaqueStateJson;

        internal List<ChatTurn> HistoryItems { get; init; } = [];
        internal string? OpaqueStateJson { get; init; }
    }

    // Internal type mirroring PluginInvokeRequest shape for deserialization in tests.
    private sealed class PluginInvokeRequest
    {
        public string AgentId { get; init; } = "";
        public string SessionId { get; init; } = "";
        public JsonElement? OpaqueState { get; init; }
    }
}
