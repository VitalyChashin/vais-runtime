// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Vais.Agents.Control.Http.Tests;

/// <summary>
/// Unit tests for <see cref="AgentEventSerializer"/>. Covers SSE event-name dispatch and
/// payload shape for representative <see cref="AgentEvent"/> subtypes; also round-trips through
/// <see cref="AgentSseParser"/> for the section-pipeline subtype added with Phase 2's
/// <see cref="RequestSectionsBuilt"/>.
/// </summary>
public sealed class AgentEventSerializerTests
{
    private static readonly DateTimeOffset _at = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly AgentContext _ctx = AgentContext.Empty with { RunId = "run-42", AgentName = "research-helper" };

    [Fact]
    public void TurnStarted_Yields_Correct_EventName_And_UserMessage()
    {
        var evt = new TurnStarted(_at, _ctx, "what's our policy?");

        var (eventName, dataJson) = AgentEventSerializer.Serialize(evt);

        eventName.Should().Be("turn.started");
        var doc = JsonDocument.Parse(dataJson);
        doc.RootElement.GetProperty("userMessage").GetString().Should().Be("what's our policy?");
    }

    [Fact]
    public void RequestSectionsBuilt_Yields_Correct_EventName_And_Round_Trips_Through_Parser()
    {
        var sections = new SectionMeasurement[]
        {
            new("system.persona", SectionKind.SystemSegment, "PersonaContributor", Order: 0, Priority: 0,
                Chars: 48, Tokens: null, Ratio: 0.04, Outcome: PackerOutcomes.Included, DroppedChars: 0),
            new("retrieval.docs", SectionKind.SystemSegment, "KnowledgeRetrievalContextProvider", Order: null, Priority: 5,
                Chars: 1042, Tokens: 260, Ratio: 0.89, Outcome: PackerOutcomes.Dropped, DroppedChars: 1042),
        };
        var budget = new SectionBudgetSummary(
            TargetChars: 256, TargetTokens: null, UsedChars: 48, UsedTokens: null,
            UsedRatio: 0.1875, DroppedCount: 1, TruncatedCount: 0);
        var evt = new RequestSectionsBuilt(_at, _ctx, TurnIndex: 3, sections, budget);

        var (eventName, dataJson) = AgentEventSerializer.Serialize(evt);

        eventName.Should().Be("request.sections.built");

        var parsed = AgentSseParser.ParseEventFrame(eventName, Encoding.UTF8.GetBytes(dataJson));
        parsed.Should().BeOfType<RequestSectionsBuilt>();
        var rsb = (RequestSectionsBuilt)parsed!;
        rsb.TurnIndex.Should().Be(3);
        rsb.Context.RunId.Should().Be("run-42");
        rsb.Sections.Should().HaveCount(2);
        rsb.Sections[0].Id.Should().Be("system.persona");
        rsb.Sections[0].ProducerId.Should().Be("PersonaContributor");
        rsb.Sections[0].Chars.Should().Be(48);
        rsb.Sections[1].Id.Should().Be("retrieval.docs");
        rsb.Sections[1].Tokens.Should().Be(260);
        rsb.Sections[1].DroppedChars.Should().Be(1042);
        rsb.Sections[1].Outcome.Should().Be(PackerOutcomes.Dropped);
        rsb.Budget.TargetChars.Should().Be(256);
        rsb.Budget.UsedChars.Should().Be(48);
        rsb.Budget.UsedRatio.Should().BeApproximately(0.1875, 0.0001);
        rsb.Budget.DroppedCount.Should().Be(1);
    }

    [Fact]
    public void Unknown_Subtype_Throws_ArgumentException()
    {
        var unknown = new UnknownAgentEvent(_at, _ctx);

        var act = () => AgentEventSerializer.Serialize(unknown);

        act.Should().Throw<ArgumentException>().WithParameterName("evt");
    }

    [Fact]
    public void Parser_Returns_Null_For_Unknown_EventName()
    {
        var parsed = AgentSseParser.ParseEventFrame("unknown.event", Encoding.UTF8.GetBytes("{}"));
        parsed.Should().BeNull();
    }

    private sealed record UnknownAgentEvent(DateTimeOffset At, AgentContext Context) : AgentEvent(At, Context);
}
