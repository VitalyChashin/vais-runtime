// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Control;
using Xunit;

namespace Vais.Agents.Core.Tests;

/// <summary>
/// v0.6 PR 1a: <c>Vais.Agents.Control.Abstractions</c> — policy engine + audit log
/// contracts with null-default implementations. The real policy engine (OPA/Rego)
/// and audit-log sinks (logger / DB / event-bus) slot in via DI later.
/// </summary>
public sealed class ControlAbstractionsTests
{
    [Fact]
    public void PolicyDecision_Allow_Is_Stateless_Singleton()
    {
        PolicyDecision.Allow.IsAllowed.Should().BeTrue();
        PolicyDecision.Allow.Reason.Should().BeNull();
        PolicyDecision.Allow.Should().Be(PolicyDecision.Allow);
    }

    [Fact]
    public void PolicyDecision_Deny_Carries_Reason()
    {
        var deny = PolicyDecision.Deny("quota exceeded");
        deny.IsAllowed.Should().BeFalse();
        deny.Reason.Should().Be("quota exceeded");
    }

    [Fact]
    public void PolicyDecision_Deny_Rejects_Empty_Reason()
    {
        FluentActions.Invoking(() => PolicyDecision.Deny(""))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => PolicyDecision.Deny("  "))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task NullAgentPolicyEngine_Allows_Everything()
    {
        var engine = NullAgentPolicyEngine.Instance;
        var manifest = new AgentManifest("x", "1", new AgentHandlerRef("T"), Array.Empty<ProtocolBinding>(), Array.Empty<ToolRef>());

        foreach (var op in Enum.GetValues<PolicyOperation>())
        {
            var decision = await engine.EvaluateAsync(op, manifest, principal: null);
            decision.IsAllowed.Should().BeTrue($"{op} must be allow-by-default under the null engine");
        }
    }

    [Fact]
    public async Task NullAgentPolicyEngine_Accepts_Null_Manifest_And_Principal()
    {
        var decision = await NullAgentPolicyEngine.Instance.EvaluateAsync(
            PolicyOperation.Query, manifest: null, principal: null);
        decision.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task NullAuditLog_Is_No_Op()
    {
        var entry = new AuditLogEntry(
            At: DateTimeOffset.UtcNow,
            Operation: PolicyOperation.Create,
            AgentId: "x",
            AgentVersion: "1",
            PrincipalId: "alice",
            TenantId: "acme",
            Allowed: true,
            DenyReason: null,
            ErrorType: null);
        await NullAuditLog.Instance.AppendAsync(entry); // must not throw
    }

    [Fact]
    public void PolicyOperation_Has_Expected_Values()
    {
        // 7 agent verbs (Create..Evict) + 7 graph verbs (GraphCreate..GraphEvict) added in v0.19.
        Enum.GetValues<PolicyOperation>().Length.Should().Be(14);
    }
}

/// <summary>
/// A recording <see cref="IAuditLog"/> stand-in — accumulates entries so tests
/// can assert exact audit sequences. Used here for contract validation; later
/// PRs use it against the lifecycle-manager engine.
/// </summary>
public sealed class AuditLogRecordingTests
{
    [Fact]
    public async Task Recording_Audit_Log_Captures_Every_Appended_Entry()
    {
        var log = new RecordingAuditLog();
        await log.AppendAsync(Make(PolicyOperation.Create, allowed: true));
        await log.AppendAsync(Make(PolicyOperation.Invoke, allowed: false, denyReason: "quota"));

        log.Entries.Should().HaveCount(2);
        log.Entries[0].Operation.Should().Be(PolicyOperation.Create);
        log.Entries[1].Allowed.Should().BeFalse();
        log.Entries[1].DenyReason.Should().Be("quota");
    }

    private static AuditLogEntry Make(PolicyOperation op, bool allowed = true, string? denyReason = null) =>
        new(DateTimeOffset.UtcNow, op, AgentId: "x", AgentVersion: "1", PrincipalId: "test",
            TenantId: null, Allowed: allowed, DenyReason: denyReason, ErrorType: null);

    private sealed class RecordingAuditLog : IAuditLog
    {
        public List<AuditLogEntry> Entries { get; } = new();
        public ValueTask AppendAsync(AuditLogEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return ValueTask.CompletedTask;
        }
    }
}
