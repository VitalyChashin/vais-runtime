// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using FluentAssertions;
using Xunit;

namespace Vais.Agents.Runtime.Extensions.Tests.Observability;

/// <summary>
/// Unit tests for <see cref="ExtensionInvocationInstrumentation"/> covering the in-process path
/// (both happy and error cases) and metric cardinality guarantees.
/// Container-path tests live in <c>Vais.Agents.Runtime.Extensions.Container.Tests</c>.
/// </summary>
public sealed class ExtensionInvocationInstrumentationTests : IDisposable
{
    // Unique per-test-instance ID so parallel test runs can't cross-contaminate listeners.
    private readonly string _extId = $"ext-{Guid.NewGuid():N}"[..16];

    private readonly List<Activity> _activities = [];
    private readonly List<(string Name, double Value, KeyValuePair<string, object?>[] Tags)> _histograms = [];
    private readonly List<(string Name, long Value, KeyValuePair<string, object?>[] Tags)> _counters = [];

    private readonly ActivityListener _activityListener;
    private readonly MeterListener _meterListener;

    public ExtensionInvocationInstrumentationTests()
    {
        _activityListener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == ExtensionTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a =>
            {
                // Only capture activities belonging to this test instance's extension ID.
                if (a.GetTagItem("vais.extension.id") is string id && id == _extId)
                    _activities.Add(a);
            },
        };
        ActivitySource.AddActivityListener(_activityListener);

        _meterListener = new MeterListener();
        _meterListener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == ExtensionTelemetry.MeterName)
                l.EnableMeasurementEvents(instrument);
        };
        _meterListener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            // Only capture metrics for this test's extension ID.
            var t = tags.ToArray();
            if (t.Any(kv => kv.Key == "extension" && (string?)kv.Value == _extId))
                _histograms.Add((instrument.Name, value, t));
        });
        _meterListener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var t = tags.ToArray();
            if (t.Any(kv => kv.Key == "extension" && (string?)kv.Value == _extId))
                _counters.Add((instrument.Name, value, t));
        });
        _meterListener.Start();
    }

    public void Dispose()
    {
        _activityListener.Dispose();
        _meterListener.Dispose();
    }

    // ── 1. In-process happy path ─────────────────────────────────────────────

    [Fact]
    public async Task InProcess_HappyPath_EmitsSpanAndMetrics()
    {
        var registry = new ExtensionHandlerRegistry();
        await registry.SwapAsync(_extId, MakeExtensionDescriptor(_extId, "h-in", new CallNextMiddleware()));
        var composer = new DefaultExtensionChainComposer(registry, agentRegistry: null);

        var chain = await composer.GetInputChainAsync("agent-1");
        var ctx = new AgentInputContext { AgentId = "agent-1", RunId = "run-42", NodeId = "node-1", Message = "hi" };
        var nextCalled = false;
        await chain[0].InvokeAsync(ctx, () => { nextCalled = true; return Task.CompletedTask; });

        nextCalled.Should().BeTrue();

        _activities.Should().ContainSingle();
        var span = _activities[0];
        span.GetTagItem("vais.extension.id").Should().Be(_extId);
        span.GetTagItem("vais.handler.id").Should().Be("h-in");
        span.GetTagItem("vais.seam").Should().Be("agentInput");
        span.GetTagItem("vais.handler.host").Should().Be("csharp");
        span.GetTagItem("vais.agent.id").Should().Be("agent-1");
        span.GetTagItem("vais.run.id").Should().Be("run-42");
        span.GetTagItem("vais.node.id").Should().Be("node-1");
        span.GetTagItem("vais.handler.action").Should().Be("next");
        span.Status.Should().Be(ActivityStatusCode.Unset);

        _histograms.Should().ContainSingle(m => m.Name == "vais_extension_handler_invoke_duration_seconds");
        _counters.Should().ContainSingle(m => m.Name == "vais_extension_handler_invoke_total");
        var counterTags = _counters[0].Tags;
        counterTags.Should().Contain(t => t.Key == "action" && (string?)t.Value == "next");
        counterTags.Should().Contain(t => t.Key == "extension" && (string?)t.Value == _extId);
    }

    // ── 2. In-process short-circuit ──────────────────────────────────────────

    [Fact]
    public async Task InProcess_ShortCircuit_EmitsShortCircuitAction()
    {
        var registry = new ExtensionHandlerRegistry();
        await registry.SwapAsync(_extId, MakeExtensionDescriptor(_extId, "h-in", new ShortCircuitMiddleware()));
        var composer = new DefaultExtensionChainComposer(registry, agentRegistry: null);

        var chain = await composer.GetInputChainAsync("agent-1");
        var nextCalled = false;
        await chain[0].InvokeAsync(
            new AgentInputContext { AgentId = "agent-1", RunId = "run-1", Message = "hi" },
            () => { nextCalled = true; return Task.CompletedTask; });

        nextCalled.Should().BeFalse("short-circuit skips next");
        _activities.Should().ContainSingle();
        _activities[0].GetTagItem("vais.handler.action").Should().Be("shortCircuit");
        _activities[0].Status.Should().Be(ActivityStatusCode.Unset);
    }

    // ── 3. In-process throw, failureMode=fail ────────────────────────────────

    [Fact]
    public async Task InProcess_Throw_FailModeFail_SpanError_ExceptionPropagates()
    {
        var registry = new ExtensionHandlerRegistry();
        await registry.SwapAsync(_extId,
            MakeExtensionDescriptor(_extId, "h-in", new ThrowingMiddleware(new InvalidOperationException("boom")), "fail"));
        var composer = new DefaultExtensionChainComposer(registry, agentRegistry: null);

        var chain = await composer.GetInputChainAsync("agent-1");
        var ctx = new AgentInputContext { AgentId = "agent-1", RunId = "run-1", Message = "hi" };

        var act = async () => await chain[0].InvokeAsync(ctx, () => Task.CompletedTask);
        await act.Should().ThrowAsync<InvalidOperationException>("failureMode=fail propagates");

        _activities.Should().ContainSingle();
        _activities[0].GetTagItem("vais.handler.action").Should().Be("fail");
        _activities[0].Status.Should().Be(ActivityStatusCode.Error);
    }

    // ── 4. In-process throw, failureMode=skip ───────────────────────────────

    [Fact]
    public async Task InProcess_Throw_FailModeSkip_SpanError_NextInvoked()
    {
        var registry = new ExtensionHandlerRegistry();
        await registry.SwapAsync(_extId,
            MakeExtensionDescriptor(_extId, "h-in", new ThrowingMiddleware(new InvalidOperationException("oops")), "skip"));
        var composer = new DefaultExtensionChainComposer(registry, agentRegistry: null);

        var chain = await composer.GetInputChainAsync("agent-1");
        var nextCalled = false;
        var ctx = new AgentInputContext { AgentId = "agent-1", RunId = "run-1", Message = "hi" };
        await chain[0].InvokeAsync(ctx, () => { nextCalled = true; return Task.CompletedTask; });

        nextCalled.Should().BeTrue("failureMode=skip must call next");
        _activities.Should().ContainSingle();
        _activities[0].GetTagItem("vais.handler.action").Should().Be("skip");
        _activities[0].Status.Should().Be(ActivityStatusCode.Error, "skip carries the originating exception");
    }

    // ── 5. Cardinality: no agent_id label on metrics ──────────────────────────

    [Fact]
    public async Task Metrics_NoAgentIdLabel_BoundedCardinality()
    {
        var registry = new ExtensionHandlerRegistry();
        await registry.SwapAsync(_extId, MakeExtensionDescriptor(_extId, "h-in", new CallNextMiddleware()));

        // Invoke the same extension handler from 5 different agent IDs.
        for (var i = 0; i < 5; i++)
        {
            var composer = new DefaultExtensionChainComposer(registry, agentRegistry: null);
            var chain = await composer.GetInputChainAsync($"agent-{i}");
            var ctx = new AgentInputContext { AgentId = $"agent-{i}", RunId = $"run-{i}", Message = "hi" };
            await chain[0].InvokeAsync(ctx, () => Task.CompletedTask);
        }

        var counterRecords = _counters.Where(m => m.Name == "vais_extension_handler_invoke_total").ToList();
        counterRecords.Should().HaveCount(5);

        foreach (var record in counterRecords)
        {
            record.Tags.Should().NotContain(t => t.Key == "agent_id" || t.Key == "agentId",
                "agent_id must NOT be a metric label to avoid cardinality explosion");
            record.Tags.Should().Contain(t => t.Key == "extension");
            record.Tags.Should().Contain(t => t.Key == "handler");
        }

        // All 5 records share the same label set (no agentId dimension).
        var firstLabels = counterRecords[0].Tags.Select(t => (t.Key, t.Value?.ToString())).ToHashSet();
        foreach (var record in counterRecords.Skip(1))
        {
            var labels = record.Tags.Select(t => (t.Key, t.Value?.ToString())).ToHashSet();
            labels.Should().BeEquivalentTo(firstLabels, "metric labels must not vary by agentId");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ExtensionDescriptor MakeExtensionDescriptor(
        string id, string handlerId, AgentInputMiddleware mw, string failureMode = "log")
    {
        var manifest = new ExtensionManifest(
            Id: id, Version: "1.0.0",
            Spec: new ExtensionSpec
            {
                Host = "csharp",
                Handlers = new List<ExtensionHandler> { new() { Id = handlerId, Seam = "agentInput" } },
            });
        var binding = new HandlerBinding(handlerId, "agentInput", 100, failureMode, mw);
        return new ExtensionDescriptor(id, "1.0.0", manifest, [binding],
            new ExtensionAssemblyLoadContext(typeof(ExtensionInvocationInstrumentationTests).Assembly.Location));
    }

    private sealed class CallNextMiddleware : AgentInputMiddleware
    {
        public override Task InvokeAsync(AgentInputContext ctx, Func<Task> next, CancellationToken ct = default)
            => next();
    }

    private sealed class ShortCircuitMiddleware : AgentInputMiddleware
    {
        public override Task InvokeAsync(AgentInputContext ctx, Func<Task> next, CancellationToken ct = default)
            => Task.CompletedTask; // does NOT call next
    }

    private sealed class ThrowingMiddleware(Exception ex) : AgentInputMiddleware
    {
        public override Task InvokeAsync(AgentInputContext ctx, Func<Task> next, CancellationToken ct = default)
            => Task.FromException(ex);
    }
}
