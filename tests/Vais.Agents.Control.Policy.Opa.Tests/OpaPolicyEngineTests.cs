// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Vais.Agents.Control.Policy.Opa.Tests;

public sealed class OpaPolicyEngineTests
{
    [Fact]
    public async Task BoolResultTrue_MapsToAllow()
    {
        var (engine, handler) = BuildEngine();
        handler.EnqueueResponse(HttpStatusCode.OK, """{"result": true}""");

        var decision = await engine.EvaluateAsync(PolicyOperation.Invoke, SampleManifest(), SamplePrincipal());

        decision.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task BoolResultFalse_MapsToDenyWithGenericReason()
    {
        var (engine, handler) = BuildEngine();
        handler.EnqueueResponse(HttpStatusCode.OK, """{"result": false}""");

        var decision = await engine.EvaluateAsync(PolicyOperation.Invoke, SampleManifest(), SamplePrincipal());

        decision.IsAllowed.Should().BeFalse();
        decision.Reason.Should().Be(OpaResponseParser.DefaultDenyReason);
    }

    [Fact]
    public async Task ObjectResultAllowed_MapsToAllow()
    {
        var (engine, handler) = BuildEngine();
        handler.EnqueueResponse(HttpStatusCode.OK, """{"result": {"allowed": true, "reason": "clearance level sufficient"}}""");

        var decision = await engine.EvaluateAsync(PolicyOperation.Create, SampleManifest(), SamplePrincipal());

        decision.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task ObjectResultDeniedWithReason_CarriesReasonThrough()
    {
        var (engine, handler) = BuildEngine();
        handler.EnqueueResponse(HttpStatusCode.OK, """{"result": {"allowed": false, "reason": "budget cap exceeded"}}""");

        var decision = await engine.EvaluateAsync(PolicyOperation.Create, SampleManifest(), SamplePrincipal());

        decision.IsAllowed.Should().BeFalse();
        decision.Reason.Should().Be("budget cap exceeded");
    }

    [Fact]
    public async Task Status4xx_ThrowsInvalidOperationException_AsAdapterBug()
    {
        var (engine, handler) = BuildEngine();
        handler.EnqueueResponse(HttpStatusCode.NotFound, """{"code": "undefined_policy"}""");

        await FluentActions
            .Awaiting(() => engine.EvaluateAsync(PolicyOperation.Invoke, SampleManifest(), SamplePrincipal()).AsTask())
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*404*");
    }

    [Fact]
    public async Task Status5xx_FailModeClosed_Denies()
    {
        var (engine, handler) = BuildEngine(opt => opt.FailMode = OpaFailMode.Closed);
        handler.EnqueueResponse(HttpStatusCode.InternalServerError, "upstream boom");

        var decision = await engine.EvaluateAsync(PolicyOperation.Invoke, SampleManifest(), SamplePrincipal());

        decision.IsAllowed.Should().BeFalse();
        decision.Reason.Should().Contain("500");
    }

    [Fact]
    public async Task Status5xx_FailModeOpen_Allows()
    {
        var (engine, handler) = BuildEngine(opt => opt.FailMode = OpaFailMode.Open);
        handler.EnqueueResponse(HttpStatusCode.ServiceUnavailable, "nope");

        var decision = await engine.EvaluateAsync(PolicyOperation.Invoke, SampleManifest(), SamplePrincipal());

        decision.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task MalformedResultBody_FailModeClosed_Denies()
    {
        var (engine, handler) = BuildEngine(opt => opt.FailMode = OpaFailMode.Closed);
        handler.EnqueueResponse(HttpStatusCode.OK, """{"something": "else"}""");

        var decision = await engine.EvaluateAsync(PolicyOperation.Invoke, SampleManifest(), SamplePrincipal());

        decision.IsAllowed.Should().BeFalse();
        decision.Reason.Should().Contain("malformed");
    }

    [Fact]
    public async Task CacheHit_ShortCircuitsSecondCall()
    {
        var (engine, handler) = BuildEngine(opt => opt.DecisionCacheTtl = TimeSpan.FromSeconds(10));
        handler.EnqueueResponse(HttpStatusCode.OK, """{"result": true}""");
        // No second response enqueued — if the engine hits HTTP twice, the test throws.

        var first = await engine.EvaluateAsync(PolicyOperation.Invoke, SampleManifest(), SamplePrincipal());
        var second = await engine.EvaluateAsync(PolicyOperation.Invoke, SampleManifest(), SamplePrincipal());

        first.IsAllowed.Should().BeTrue();
        second.IsAllowed.Should().BeTrue();
        handler.RequestCount.Should().Be(1);
    }

    [Fact]
    public async Task ZeroTtl_EveryCall_HitsNetwork()
    {
        var (engine, handler) = BuildEngine(opt => opt.DecisionCacheTtl = TimeSpan.Zero);
        handler.EnqueueResponse(HttpStatusCode.OK, """{"result": true}""");
        handler.EnqueueResponse(HttpStatusCode.OK, """{"result": true}""");

        _ = await engine.EvaluateAsync(PolicyOperation.Invoke, SampleManifest(), SamplePrincipal());
        _ = await engine.EvaluateAsync(PolicyOperation.Invoke, SampleManifest(), SamplePrincipal());

        handler.RequestCount.Should().Be(2);
    }

    [Fact]
    public async Task RequestBody_CarriesInputSchemaV1_WithOperationPrincipalAgent()
    {
        var (engine, handler) = BuildEngine();
        handler.EnqueueResponse(HttpStatusCode.OK, """{"result": true}""");

        _ = await engine.EvaluateAsync(PolicyOperation.Invoke, SampleManifest(), SamplePrincipal());

        handler.LastRequestBody.Should().NotBeNull();
        handler.LastRequestBody!.Should().Contain("\"schemaVersion\":\"1\"");
        handler.LastRequestBody.Should().Contain("\"operation\":\"Invoke\"");
        handler.LastRequestBody.Should().Contain("\"principal\"");
        handler.LastRequestBody.Should().Contain("\"agent\"");
    }

    [Fact]
    public async Task RequestPath_UsesConfiguredDataPath()
    {
        var (engine, handler) = BuildEngine(opt => opt.DataPath = "custom/team/allow");
        handler.EnqueueResponse(HttpStatusCode.OK, """{"result": true}""");

        _ = await engine.EvaluateAsync(PolicyOperation.Invoke, SampleManifest(), SamplePrincipal());

        handler.LastRequestUri!.AbsolutePath.Should().Be("/v1/data/custom/team/allow");
    }

    private static AgentManifest SampleManifest() => new(
        Id: "chat",
        Version: "v1",
        Handler: new AgentHandlerRef("Vais.Agents.Samples.ChatAgent"),
        Protocols: new[] { new ProtocolBinding("Http") },
        Tools: new[] { new ToolRef("weather") });

    private static AgentPrincipal SamplePrincipal() => new(
        Id: "u1",
        TenantId: "tenant-42",
        Scopes: new[] { "agent:invoke" });

    private static (OpaPolicyEngine engine, RecordingHttpMessageHandler handler) BuildEngine(
        Action<OpaPolicyEngineOptions>? configure = null)
    {
        var options = new OpaPolicyEngineOptions
        {
            BaseUrl = new Uri("http://opa.test:8181"),
            LogPolicyVersionOnStartup = false,
            DecisionCacheTtl = TimeSpan.Zero,
            Timeout = TimeSpan.FromSeconds(5),
        };
        configure?.Invoke(options);

        var handler = new RecordingHttpMessageHandler();
        var client = new HttpClient(handler) { BaseAddress = options.BaseUrl };
        var monitor = new StubOptionsMonitor(options);
        var engine = new OpaPolicyEngine(
            client,
            monitor,
            TimeProvider.System,
            NullLogger<OpaPolicyEngine>.Instance);
        return (engine, handler);
    }

    private sealed class StubOptionsMonitor(OpaPolicyEngineOptions value) : IOptionsMonitor<OpaPolicyEngineOptions>
    {
        public OpaPolicyEngineOptions CurrentValue => value;
        public OpaPolicyEngineOptions Get(string? name) => value;
        public IDisposable OnChange(Action<OpaPolicyEngineOptions, string?> listener) => Noop.Instance;

        private sealed class Noop : IDisposable
        {
            public static readonly Noop Instance = new();
            public void Dispose() { }
        }
    }

    internal sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new();

        public int RequestCount { get; private set; }
        public Uri? LastRequestUri { get; private set; }
        public string? LastRequestBody { get; private set; }

        public void EnqueueResponse(HttpStatusCode status, string body)
            => _responses.Enqueue((status, body));

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequestUri = request.RequestUri;
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("Unexpected HTTP request — no response enqueued.");
            }
            var (status, body) = _responses.Dequeue();
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
