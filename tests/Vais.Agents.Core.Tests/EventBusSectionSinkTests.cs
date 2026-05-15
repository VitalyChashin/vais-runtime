// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Hosting.InMemory;
using Xunit;

namespace Vais.Agents.Core.Tests;

public sealed class EventBusSectionSinkTests
{
    private static SectionMeasurement M(string id, string outcome = "included", int chars = 0, double ratio = 0)
        => new(id, SectionKind.SystemSegment, ProducerId: null, Order: null, Priority: null, chars, Tokens: null, ratio, outcome, DroppedChars: 0);

    private static SectionTelemetrySnapshot Snap(AgentContext context, params SectionMeasurement[] sections)
        => new(
            Context: context,
            TurnIndex: 1,
            Sections: sections,
            Budget: new SectionBudgetSummary(null, null, sections.Sum(s => s.Chars), null, 0.0, 0, 0));

    [Fact]
    public void Null_Bus_Throws()
    {
        Action act = () => new EventBusSectionSink(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Publishes_RequestSectionsBuilt_Event_With_Snapshot_Fields()
    {
        var bus = new InMemoryAgentEventBus();
        var captured = new List<AgentEvent>();
        using var sub = bus.Subscribe((evt, _) => { captured.Add(evt); return ValueTask.CompletedTask; });

        var sink = new EventBusSectionSink(bus);
        var ctx = new AgentContext { RunId = "run-1", AgentName = "agent-a" };
        await sink.EmitAsync(Snap(ctx,
            M("system.persona", outcome: "included", chars: 10, ratio: 0.4),
            M("retrieval.docs", outcome: "dropped", chars: 15, ratio: 0.6)));

        captured.Should().ContainSingle();
        var built = captured[0].Should().BeOfType<RequestSectionsBuilt>().Subject;
        built.Context.RunId.Should().Be("run-1");
        built.Context.AgentName.Should().Be("agent-a");
        built.TurnIndex.Should().Be(1);
        built.Sections.Should().HaveCount(2);
        built.Sections[0].Id.Should().Be("system.persona");
        built.Sections[1].Outcome.Should().Be("dropped");
    }

    [Fact]
    public async Task Sink_Reaches_StatefulAiAgent_When_Wired_Via_Options()
    {
        // End-to-end via the section pipeline: configure the EventBusSectionSink as a sink,
        // run AskAsync, observe RequestSectionsBuilt landing on the bus alongside the existing
        // TurnStarted / TurnCompleted events.
        var bus = new InMemoryAgentEventBus();
        var captured = new List<AgentEvent>();
        using var sub = bus.Subscribe((evt, _) => { captured.Add(evt); return ValueTask.CompletedTask; });

        var sink = new EventBusSectionSink(bus);
        var provider = new FakeCompletionProvider(_ => new CompletionResponse("ok"));
        var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
        {
            SystemPrompt = "base",
            EventBus = bus,
            SectionTelemetrySinks = new[] { (ISectionTelemetrySink)sink },
        });

        await agent.AskAsync("hi");

        var sectionsBuiltEvents = captured.OfType<RequestSectionsBuilt>().ToList();
        sectionsBuiltEvents.Should().ContainSingle();
        sectionsBuiltEvents[0].Sections.Select(s => s.Id).Should().Contain("system.base");
    }

    [Fact]
    public async Task Carries_Full_AgentContext_To_Subscribers()
    {
        // The event's Context exposes more than RunId/AgentName: subscribers can read UserId,
        // TenantId, WorkspaceId, CorrelationId — same surface as the other AgentEvent subtypes.
        var bus = new InMemoryAgentEventBus();
        var captured = new List<AgentEvent>();
        using var sub = bus.Subscribe((evt, _) => { captured.Add(evt); return ValueTask.CompletedTask; });

        var sink = new EventBusSectionSink(bus);
        var ctx = new AgentContext(
            UserId: "u-7",
            TenantId: "t-3",
            CorrelationId: "corr-x",
            AgentName: "agent-a")
        {
            RunId = "run-1",
            WorkspaceId = "ws-9",
        };

        await sink.EmitAsync(Snap(ctx, M("a", chars: 5, ratio: 1.0)));

        var built = captured.Should().ContainSingle().Subject.Should().BeOfType<RequestSectionsBuilt>().Subject;
        built.Context.UserId.Should().Be("u-7");
        built.Context.TenantId.Should().Be("t-3");
        built.Context.CorrelationId.Should().Be("corr-x");
        built.Context.WorkspaceId.Should().Be("ws-9");
    }
}
