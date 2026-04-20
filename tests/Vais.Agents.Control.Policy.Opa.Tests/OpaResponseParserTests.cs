// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Control;
using Vais.Agents.Control.Policy.Opa;
using Xunit;

namespace Vais.Agents.Control.Policy.Opa.Tests;

public sealed class OpaResponseParserTests
{
    [Fact]
    public void BoolResultTrue_MapsToAllow()
    {
        var decision = OpaResponseParser.Parse("""{"result": true}""");

        decision.Should().NotBeNull();
        decision!.Value.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public void BoolResultFalse_MapsToDenyWithDefaultReason()
    {
        var decision = OpaResponseParser.Parse("""{"result": false}""");

        decision.Should().NotBeNull();
        decision!.Value.IsAllowed.Should().BeFalse();
        decision.Value.Reason.Should().Be(OpaResponseParser.DefaultDenyReason);
    }

    [Fact]
    public void ObjectResultAllowed_MapsToAllow()
    {
        var decision = OpaResponseParser.Parse("""{"result": {"allowed": true, "reason": "clearance level sufficient"}}""");

        decision.Should().NotBeNull();
        decision!.Value.IsAllowed.Should().BeTrue();
        decision.Value.Reason.Should().BeNull();
    }

    [Fact]
    public void ObjectResultDeniedWithReason_CarriesReasonThrough()
    {
        var decision = OpaResponseParser.Parse("""{"result": {"allowed": false, "reason": "budget cap exceeded"}}""");

        decision.Should().NotBeNull();
        decision!.Value.IsAllowed.Should().BeFalse();
        decision.Value.Reason.Should().Be("budget cap exceeded");
    }

    [Fact]
    public void ObjectResultDeniedWithoutReason_FallsBackToDefault()
    {
        var decision = OpaResponseParser.Parse("""{"result": {"allowed": false}}""");

        decision.Should().NotBeNull();
        decision!.Value.IsAllowed.Should().BeFalse();
        decision.Value.Reason.Should().Be(OpaResponseParser.DefaultDenyReason);
    }

    [Fact]
    public void MissingResultProperty_ReturnsNull()
    {
        OpaResponseParser.Parse("""{"other": "value"}""").Should().BeNull();
    }

    [Fact]
    public void MalformedJson_ReturnsNull()
    {
        OpaResponseParser.Parse("not json at all").Should().BeNull();
    }

    [Fact]
    public void ArrayResult_ReturnsNull_Unsupported()
    {
        OpaResponseParser.Parse("""{"result": ["foo"]}""").Should().BeNull();
    }

    [Fact]
    public void ObjectResultWithoutAllowed_ReturnsNull()
    {
        OpaResponseParser.Parse("""{"result": {"reason": "nope"}}""").Should().BeNull();
    }

    [Fact]
    public void EmptyBody_ReturnsNull()
    {
        OpaResponseParser.Parse("").Should().BeNull();
    }
}
