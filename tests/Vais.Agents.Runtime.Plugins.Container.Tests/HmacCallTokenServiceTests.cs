// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Vais.Agents.Core;
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
        // ttlSeconds = -1 → expiresAt = UtcNow - 1 s (already past)
        var token = svc.Generate("run-1", "agent-1", -1);
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
        var token = svc.Generate("run-1", "agent-1", -1);
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

    // ── G4: AgentContextClaims round-trip + backwards-compat + tamper ────────

    [Fact]
    public void Generate_WithClaims_RoundTripsEveryField()
    {
        var svc = MakeService();
        var claims = new AgentContextClaims(
            UserId: "user-1",
            TenantId: "tenant-acme",
            CorrelationId: "corr-xyz",
            WorkspaceId: "workspace-default",
            PrivilegeLevel: PrivilegeLevel.Platform,
            AutonomyLevel: AutonomyLevel.RequiresApproval,
            Scopes: new[] { "read:files", "write:files", "admin:agents" },
            AllowedTools: new[] { "tavily_search", "fetch_url", "summarise" },
            MaxChainDepth: 7,
            BaselineRunId: "baseline-run-99");

        var token = svc.Generate("run-1", "agent-1", claims, 60);
        var ok = svc.TryExtract(token, out var runId, out var agentId, out var leaseId, out var extracted);

        ok.Should().BeTrue();
        runId.Should().Be("run-1");
        agentId.Should().Be("agent-1");
        leaseId.Should().Be(string.Empty, because: "no leaseId in the v1-shaped claims-bearing path");
        extracted.Should().BeEquivalentTo(claims);
    }

    [Fact]
    public void Generate_WithClaims_SessionMode_RoundTripsLeaseIdAndClaims()
    {
        var svc = MakeService();
        var claims = new AgentContextClaims(
            UserId: "user-2", TenantId: "tenant-b", CorrelationId: null, WorkspaceId: "ws-1",
            PrivilegeLevel: PrivilegeLevel.Workspace, AutonomyLevel: AutonomyLevel.Supervised,
            Scopes: new[] { "agent:invoke" }, AllowedTools: new[] { "search" },
            MaxChainDepth: 3, BaselineRunId: null);

        var token = svc.Generate("run-s", "agent-s", "lease-42", claims, 60);
        var ok = svc.TryExtract(token, out var runId, out var agentId, out var leaseId, out var extracted);

        ok.Should().BeTrue();
        runId.Should().Be("run-s");
        agentId.Should().Be("agent-s");
        leaseId.Should().Be("lease-42");
        extracted.Should().BeEquivalentTo(claims);
    }

    [Fact]
    public void Generate_WithClaims_AllNullFields_RoundTripsAsEmpty()
    {
        var svc = MakeService();
        var token = svc.Generate("run-empty", "agent-empty", AgentContextClaims.Empty, 60);
        var ok = svc.TryExtract(token, out var runId, out var agentId, out var leaseId, out var extracted);

        ok.Should().BeTrue();
        runId.Should().Be("run-empty");
        agentId.Should().Be("agent-empty");
        extracted.Should().NotBeNull();
        extracted!.UserId.Should().BeNull();
        extracted.Scopes.Should().BeNull();
        extracted.AllowedTools.Should().BeNull();
        extracted.PrivilegeLevel.Should().BeNull();
    }

    [Fact]
    public void Generate_WithClaims_LargeAllowedToolsList_StaysWithinReasonableSize()
    {
        var svc = MakeService();
        var allowed = Enumerable.Range(0, 50).Select(i => $"tool-{i:000}").ToArray();
        var claims = AgentContextClaims.Empty with { AllowedTools = allowed };
        var token = svc.Generate("run-big", "agent-big", claims, 60);

        svc.TryExtract(token, out _, out _, out _, out var extracted).Should().BeTrue();
        extracted!.AllowedTools.Should().HaveCount(50);
        // Sanity bound: 50 short tool names + JSON overhead should fit well under 4 KB on the wire.
        token.Length.Should().BeLessThan(4096);
    }

    [Fact]
    public void TryExtract_LegacyTwoSegmentToken_ReturnsNullClaims()
    {
        var svc = MakeService();
        // Mint the legacy v2 way (no claims).
        var legacyToken = svc.Generate("run-legacy", "agent-legacy", "lease-old", 60);
        legacyToken.Count(c => c == '.').Should().Be(1, because: "legacy tokens have a single dot separator");

        var ok = svc.TryExtract(legacyToken, out var runId, out var agentId, out var leaseId, out var claims);

        ok.Should().BeTrue();
        runId.Should().Be("run-legacy");
        agentId.Should().Be("agent-legacy");
        leaseId.Should().Be("lease-old");
        claims.Should().BeNull(because: "legacy tokens carry no claims segment; readers must tolerate null");
    }

    [Fact]
    public void TryExtract_ClaimsBearingToken_LegacyOverloadStillExtractsRunAndAgent()
    {
        // The 3-arg and 4-arg TryExtract overloads must continue to work on v3 tokens —
        // they simply discard the claims via the underlying 5-arg call.
        var svc = MakeService();
        var token = svc.Generate(
            "run-mix", "agent-mix",
            AgentContextClaims.Empty with { UserId = "u" },
            60);

        svc.TryExtract(token, out var r, out var a).Should().BeTrue();
        r.Should().Be("run-mix");
        a.Should().Be("agent-mix");
    }

    [Fact]
    public void TryExtract_TamperedClaimsSegment_FailsHmac()
    {
        var svc = MakeService();
        var claims = AgentContextClaims.Empty with { PrivilegeLevel = PrivilegeLevel.Platform };
        var token = svc.Generate("run-1", "agent-1", claims, 60);

        // Flip a byte in the claims segment (between the first and second dot).
        var firstDot = token.IndexOf('.');
        var secondDot = token.IndexOf('.', firstDot + 1);
        var idx = firstDot + 1; // first byte of claims segment
        var tampered = token[..idx] + (char)(token[idx] ^ 1) + token[(idx + 1)..secondDot] + token[secondDot..];

        svc.TryExtract(tampered, out _, out _, out _, out _).Should().BeFalse(
            because: "HMAC covers payload + claims; tampering with claims must fail validation");
    }

    [Fact]
    public void RenewalFlow_ClaimsSurviveReMint()
    {
        // Simulate the /token/renew handler: extract claims from the old token, re-Generate with them.
        // This is the load-bearing property that makes Option A self-contained — no separate store.
        var svc = MakeService();
        var original = new AgentContextClaims(
            UserId: "user-renew", TenantId: "t", CorrelationId: "c", WorkspaceId: "w",
            PrivilegeLevel: PrivilegeLevel.Workspace, AutonomyLevel: AutonomyLevel.FullyAutonomous,
            Scopes: new[] { "s1", "s2" }, AllowedTools: new[] { "t1" },
            MaxChainDepth: 5, BaselineRunId: "br");

        var oldToken = svc.Generate("run-r", "agent-r", "lease-r", original, 60);
        svc.TryExtract(oldToken, out var r, out var a, out var lease, out var claimsFromOld).Should().BeTrue();

        var newToken = svc.Generate(r, a, lease, claimsFromOld!, 60);
        svc.TryExtract(newToken, out _, out _, out _, out var claimsFromNew).Should().BeTrue();

        claimsFromNew.Should().BeEquivalentTo(original);
    }
}
