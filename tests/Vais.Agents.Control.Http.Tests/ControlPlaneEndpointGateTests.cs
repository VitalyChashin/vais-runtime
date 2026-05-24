// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Vais.Agents.Core;
using Xunit;

namespace Vais.Agents.Control.Http.Tests;

/// <summary>
/// NB-1 / NB-3b: the endpoint-level policy + audit gate used by the two kinds
/// without a lifecycle manager (EvalSuite, Extension). Verifies deny → 403 + audit,
/// allow → null + audit, scope propagation, and that the same gate yields the same
/// decision for any kind (the structural REST/MCP parity guarantee).
/// </summary>
public sealed class ControlPlaneEndpointGateTests
{
    [Fact]
    public async Task Denies_With_403_And_Audits_When_Policy_Denies()
    {
        var audit = new RecordingAuditLog();
        var http = BuildContext(
            new DenyPolicy("nope"),
            audit,
            new AgentContext(UserId: "alice", TenantId: "acme") { Scopes = new[] { "vais.read" } });

        var result = await ControlPlaneEndpointGate.CheckAsync(
            http, PolicyOperation.EvalSuiteUpsert, "suite-1", "1", default);

        result.Should().BeOfType<ProblemHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);

        audit.Entries.Should().ContainSingle();
        audit.Entries[0].Allowed.Should().BeFalse();
        audit.Entries[0].Operation.Should().Be(PolicyOperation.EvalSuiteUpsert);
        audit.Entries[0].AgentId.Should().Be("suite-1");
        audit.Entries[0].PrincipalId.Should().Be("alice");
        audit.Entries[0].DenyReason.Should().Be("nope");
    }

    [Fact]
    public async Task Allows_With_Null_Result_And_Audits_When_Policy_Allows()
    {
        var audit = new RecordingAuditLog();
        var http = BuildContext(AllowPolicy.Instance, audit, new AgentContext(UserId: "bob"));

        var result = await ControlPlaneEndpointGate.CheckAsync(
            http, PolicyOperation.ExtensionEvict, "ext-1", null, default);

        result.Should().BeNull("allow returns null so the caller proceeds");
        audit.Entries.Should().ContainSingle();
        audit.Entries[0].Allowed.Should().BeTrue();
        audit.Entries[0].Operation.Should().Be(PolicyOperation.ExtensionEvict);
    }

    [Fact]
    public async Task Same_Denying_Policy_Denies_Every_Caller_Kind()
    {
        // The shared gate yields the same decision regardless of which control-plane
        // kind invokes it — the structural guarantee that the REST endpoints and the
        // (Phase 4) MCP verbs cannot diverge once both route through this seam.
        var policy = new DenyPolicy("denied");

        var evalResult = await ControlPlaneEndpointGate.CheckAsync(
            BuildContext(policy, new RecordingAuditLog(), new AgentContext(UserId: "alice")),
            PolicyOperation.EvalSuiteUpsert, "suite-1", "1", default);
        var extResult = await ControlPlaneEndpointGate.CheckAsync(
            BuildContext(policy, new RecordingAuditLog(), new AgentContext(UserId: "alice")),
            PolicyOperation.ExtensionUpdate, "ext-1", "1", default);

        evalResult.Should().BeOfType<ProblemHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        extResult.Should().BeOfType<ProblemHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Passes_Scopes_From_Context_To_Policy()
    {
        var recording = new RecordingPolicy();
        var http = BuildContext(
            recording,
            new RecordingAuditLog(),
            new AgentContext(UserId: "alice") { Scopes = new[] { "vais.author:EvalSuite" } });

        await ControlPlaneEndpointGate.CheckAsync(http, PolicyOperation.EvalSuiteUpsert, "suite-1", "1", default);

        recording.Seen.Should().ContainSingle();
        recording.Seen[0]!.Scopes.Should().BeEquivalentTo("vais.author:EvalSuite");
    }

    private static DefaultHttpContext BuildContext(IAgentPolicyEngine policy, IAuditLog audit, AgentContext ctx)
    {
        var accessor = new AsyncLocalAgentContextAccessor();
        _ = accessor.Push(ctx); // sets the ambient context for this async flow (test scope)

        var services = new ServiceCollection();
        services.AddSingleton(policy);
        services.AddSingleton(audit);
        services.AddSingleton<IAgentContextAccessor>(accessor);

        return new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            Request = { Path = "/v1/test" },
        };
    }

    private sealed class DenyPolicy(string reason) : IAgentPolicyEngine
    {
        public ValueTask<PolicyDecision> EvaluateAsync(
            PolicyOperation operation, AgentManifest? manifest, AgentPrincipal? principal, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(PolicyDecision.Deny(reason));
    }

    private sealed class AllowPolicy : IAgentPolicyEngine
    {
        public static readonly AllowPolicy Instance = new();
        public ValueTask<PolicyDecision> EvaluateAsync(
            PolicyOperation operation, AgentManifest? manifest, AgentPrincipal? principal, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(PolicyDecision.Allow);
    }

    private sealed class RecordingPolicy : IAgentPolicyEngine
    {
        public List<AgentPrincipal?> Seen { get; } = new();
        public ValueTask<PolicyDecision> EvaluateAsync(
            PolicyOperation operation, AgentManifest? manifest, AgentPrincipal? principal, CancellationToken cancellationToken = default)
        {
            Seen.Add(principal);
            return ValueTask.FromResult(PolicyDecision.Allow);
        }
    }

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
