// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace Vais.Agents.Runtime.Plugins.Container.Tests;

public sealed class HmacCallTokenServiceTests
{
    private const string ValidSecret = "A32CharacterSecretKeyForTestingXX";

    private static HmacCallTokenService MakeService(string? secret = ValidSecret)
    {
        var config = Substitute.For<IConfiguration>();
        config["Vais:ContainerPlugin:CallTokenSecret"].Returns(secret);
        return new HmacCallTokenService(config);
    }

    [Fact]
    public void Generate_Validate_RoundTrip_ReturnsTrue()
    {
        var svc = MakeService();
        var token = svc.Generate("run-1", "agent-1", 60);
        svc.Validate(token, "run-1", "agent-1").Should().BeTrue();
    }

    [Fact]
    public void Validate_ExpiredToken_ReturnsFalse()
    {
        var svc = MakeService();
        // timeoutSeconds = -31 → expiresAt = UtcNow - 1 s (already past)
        var token = svc.Generate("run-1", "agent-1", -31);
        svc.Validate(token, "run-1", "agent-1").Should().BeFalse();
    }

    [Fact]
    public void Validate_WrongAgentId_ReturnsFalse()
    {
        var svc = MakeService();
        var token = svc.Generate("run-1", "agent-1", 60);
        svc.Validate(token, "run-1", "other-agent").Should().BeFalse();
    }

    [Fact]
    public void Validate_WrongRunId_ReturnsFalse()
    {
        var svc = MakeService();
        var token = svc.Generate("run-1", "agent-1", 60);
        svc.Validate(token, "run-2", "agent-1").Should().BeFalse();
    }

    [Fact]
    public void Validate_TamperedPayload_ReturnsFalse()
    {
        var svc = MakeService();
        var token = svc.Generate("run-1", "agent-1", 60);
        // Flip the first character of the payload segment so HMAC no longer matches.
        var tampered = (char)(token[0] ^ 1) + token[1..];
        svc.Validate(tampered, "run-1", "agent-1").Should().BeFalse();
    }

    [Fact]
    public void Validate_MissingDotSeparator_ReturnsFalse()
    {
        var svc = MakeService();
        svc.Validate("notadottoken", "run-1", "agent-1").Should().BeFalse();
    }

    [Fact]
    public void Validate_EmptyToken_ReturnsFalse()
    {
        var svc = MakeService();
        svc.Validate("", "run-1", "agent-1").Should().BeFalse();
    }

    [Fact]
    public void Constructor_MissingSecret_Throws()
    {
        var config = Substitute.For<IConfiguration>();
        config["Vais:ContainerPlugin:CallTokenSecret"].Returns((string?)null);
        var act = () => new HmacCallTokenService(config);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*CallTokenSecret*");
    }

    [Fact]
    public void Constructor_ShortSecret_Throws()
    {
        var config = Substitute.For<IConfiguration>();
        config["Vais:ContainerPlugin:CallTokenSecret"].Returns("tooshort");
        var act = () => new HmacCallTokenService(config);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*32 characters*");
    }

    [Fact]
    public void Generate_DifferentRunIds_ProduceDifferentTokens()
    {
        var svc = MakeService();
        var t1 = svc.Generate("run-1", "agent-1", 60);
        var t2 = svc.Generate("run-2", "agent-1", 60);
        t1.Should().NotBe(t2);
    }

    // TryExtract tests

    [Fact]
    public void TryExtract_ValidToken_ExtractsRunIdAndAgentId()
    {
        var svc = MakeService();
        var token = svc.Generate("run-42", "agent-7", 60);
        svc.TryExtract(token, out var runId, out var agentId).Should().BeTrue();
        runId.Should().Be("run-42");
        agentId.Should().Be("agent-7");
    }

    [Fact]
    public void TryExtract_ExpiredToken_ReturnsFalse()
    {
        var svc = MakeService();
        var token = svc.Generate("run-1", "agent-1", -31);
        svc.TryExtract(token, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryExtract_TamperedPayload_ReturnsFalse()
    {
        var svc = MakeService();
        var token = svc.Generate("run-1", "agent-1", 60);
        var tampered = (char)(token[0] ^ 1) + token[1..];
        svc.TryExtract(tampered, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryExtract_MalformedToken_ReturnsFalse()
    {
        var svc = MakeService();
        svc.TryExtract("not-a-token", out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryExtract_EmptyToken_ReturnsFalse()
    {
        var svc = MakeService();
        svc.TryExtract("", out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryExtract_ValidatesViaSameHmacAsValidate()
    {
        var svc = MakeService();
        var token = svc.Generate("run-x", "agent-y", 60);
        var viaValidate = svc.Validate(token, "run-x", "agent-y");
        var viaExtract = svc.TryExtract(token, out var r, out var a);
        viaValidate.Should().BeTrue();
        viaExtract.Should().BeTrue();
        r.Should().Be("run-x");
        a.Should().Be("agent-y");
    }
}
