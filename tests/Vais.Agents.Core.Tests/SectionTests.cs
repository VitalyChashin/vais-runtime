// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Xunit;

namespace Vais.Agents.Core.Tests;

public sealed class SectionTests
{
    [Theory]
    [InlineData("memory.user.long")]
    [InlineData("retrieval.docs")]
    [InlineData("system.persona")]
    [InlineData("tools.declared")]
    [InlineData("cognition.diee.goal_stack")]
    [InlineData("a")]
    [InlineData("a-b")]
    [InlineData("a_b")]
    [InlineData("MixedCase.123.with_digits-and-dashes")]
    public void SectionId_Validate_Accepts_Wellformed_Ids(string id)
    {
        SectionId.Validate(id).Should().Be(id);
    }

    [Fact]
    public void SectionId_Validate_Rejects_Null()
    {
        Action act = () => SectionId.Validate(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData(".leading", "leading dot")]
    [InlineData("trailing.", "trailing dot")]
    [InlineData("a..b", "empty segment")]
    [InlineData("with space", "space character")]
    [InlineData("has/slash", "slash character")]
    [InlineData("has:colon", "colon character")]
    [InlineData("has+plus", "plus character")]
    public void SectionId_Validate_Rejects_Malformed_Ids(string id, string reason)
    {
        Action act = () => SectionId.Validate(id);
        act.Should().Throw<ArgumentException>(because: reason);
    }

    [Fact]
    public void SectionId_Validate_Rejects_Too_Long()
    {
        var id = new string('a', SectionId.MaxLength + 1);
        Action act = () => SectionId.Validate(id);
        act.Should().Throw<ArgumentException>().WithMessage("*at most*");
    }

    [Fact]
    public void Section_Ctor_Calls_Validation_On_Id()
    {
        Action act = () => new Section("bad..id", SectionKind.SystemSegment, new TextPayload("hi"));
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Section_Ctor_Rejects_Null_Payload()
    {
        Action act = () => new Section("valid.id", SectionKind.SystemSegment, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Section_Holds_Wellformed_Inputs()
    {
        var section = new Section(
            "retrieval.docs",
            SectionKind.SystemSegment,
            new TextPayload("retrieved content"),
            Order: 10,
            ProducerId: "KnowledgeRetrievalContextProvider",
            Budget: new SectionBudget(Priority: 5, MaxChars: 1024));

        section.Id.Should().Be("retrieval.docs");
        section.Kind.Should().Be(SectionKind.SystemSegment);
        section.Payload.Should().BeOfType<TextPayload>().Which.Value.Should().Be("retrieved content");
        section.Order.Should().Be(10);
        section.ProducerId.Should().Be("KnowledgeRetrievalContextProvider");
        section.Budget.Should().NotBeNull();
        section.Budget!.Priority.Should().Be(5);
        section.Budget.MaxChars.Should().Be(1024);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(11)]
    [InlineData(int.MaxValue)]
    public void SectionBudget_Rejects_Priority_Out_Of_Range(int priority)
    {
        Action act = () => new SectionBudget(priority);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(10)]
    public void SectionBudget_Accepts_Priority_In_Range(int priority)
    {
        var budget = new SectionBudget(priority);
        budget.Priority.Should().Be(priority);
    }
}
