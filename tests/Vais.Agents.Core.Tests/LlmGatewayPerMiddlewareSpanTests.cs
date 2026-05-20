// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
using FluentAssertions;
using Xunit;

namespace Vais.Agents.Core.Tests;

/// <summary>
/// Verifies that <see cref="LlmGatewayPipeline"/> emits one
/// <c>vais.gateway.llm.middleware/&lt;Name&gt;</c> child span per middleware
/// (EXO-5 of the extensions observability plan).
/// </summary>
public sealed class LlmGatewayPerMiddlewareSpanTests : IDisposable
{
    private readonly List<Activity> _stopped = [];
    private readonly ActivityListener _listener;

    public LlmGatewayPerMiddlewareSpanTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == AgenticDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => _stopped.Add(a),
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    [Fact]
    public async Task InvokeAsync_ThreeMiddlewares_EmitsThreeChildSpans()
    {
        var provider = new EchoProvider("result");
        var mw1 = new TracedMiddleware();
        var mw2 = new TracedMiddleware();
        var mw3 = new TracedMiddleware();

        // Start a parent span so per-middleware spans nest under it.
        using var parent = AgenticDiagnostics.ActivitySource
            .StartActivity("llm.completion", ActivityKind.Internal);

        await LlmGatewayPipeline.InvokeAsync(
            new CompletionRequest([]),
            provider,
            [mw1, mw2, mw3]);

        // Each middleware must produce exactly one vais.gateway.llm.middleware/<Name> span.
        var perMwSpans = _stopped
            .Where(a => a.OperationName.StartsWith("vais.gateway.llm.middleware/"))
            .ToList();

        perMwSpans.Should().HaveCount(3);
        perMwSpans.Select(a => a.OperationName).Should().BeEquivalentTo(
        [
            $"vais.gateway.llm.middleware/{nameof(TracedMiddleware)}",
            $"vais.gateway.llm.middleware/{nameof(TracedMiddleware)}",
            $"vais.gateway.llm.middleware/{nameof(TracedMiddleware)}",
        ]);

        foreach (var span in perMwSpans)
        {
            span.GetTagItem("middleware.name").Should().Be(nameof(TracedMiddleware));
            span.GetTagItem("middleware.kind").Should().Be("builtin");
            span.Status.Should().NotBe(ActivityStatusCode.Error);
        }
    }

    [Fact]
    public async Task InvokeAsync_MiddlewareThrows_SpanMarkedError()
    {
        var provider = new EchoProvider("ok");
        var thrower = new ThrowingMiddleware(new InvalidOperationException("bad"));

        using var parent = AgenticDiagnostics.ActivitySource
            .StartActivity("llm.completion", ActivityKind.Internal);

        var act = async () => await LlmGatewayPipeline.InvokeAsync(
            new CompletionRequest([]),
            provider,
            [thrower]);

        await act.Should().ThrowAsync<InvalidOperationException>();

        var span = _stopped
            .FirstOrDefault(a => a.OperationName.StartsWith("vais.gateway.llm.middleware/"));

        span.Should().NotBeNull();
        span!.Status.Should().Be(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task InvokeAsync_NoMiddleware_NoPerMwSpans()
    {
        var provider = new EchoProvider("ok");

        await LlmGatewayPipeline.InvokeAsync(new CompletionRequest([]), provider, []);

        _stopped.Should().NotContain(a => a.OperationName.StartsWith("vais.gateway.llm.middleware/"));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private sealed class EchoProvider(string text) : ICompletionProvider
    {
        public string ProviderName => "echo";
        public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new CompletionResponse(text));
    }

    private sealed class TracedMiddleware : LlmGatewayMiddleware
    {
        protected override Task<CompletionResponse> InvokeAsync(
            CompletionRequest request,
            Func<CompletionRequest, CancellationToken, Task<CompletionResponse>> next,
            CancellationToken cancellationToken)
            => next(request, cancellationToken);
    }

    private sealed class ThrowingMiddleware(Exception ex) : LlmGatewayMiddleware
    {
        protected override Task<CompletionResponse> InvokeAsync(
            CompletionRequest request,
            Func<CompletionRequest, CancellationToken, Task<CompletionResponse>> next,
            CancellationToken cancellationToken)
            => Task.FromException<CompletionResponse>(ex);
    }
}
