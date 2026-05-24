// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Control;
using Vais.Agents.Control.InProcess;
using Xunit;

namespace Vais.Agents.Core.Tests;

/// <summary>
/// NB-5: the RBAC author-roles policy engine authorizes mutating control-plane
/// verbs against the caller's JWT scopes + the overlay's author-roles, while
/// letting all runtime / read verbs pass.
/// </summary>
public sealed class AuthorRolesPolicyEngineTests
{
    private static AuthorRolesPolicy Policy() => new()
    {
        Roles = new Dictionary<string, AuthorRole>
        {
            // Full authoring on Agent only.
            ["vais.author"] = Role(("Agent", "*")),
            // Write but not delete on Agent (per-verb granularity).
            ["agent-writer"] = Role(("Agent", "write")),
            // Read-only: no permissions at all.
            ["vais.readonly"] = new AuthorRole { Permissions = new Dictionary<string, IReadOnlyList<string>>() },
        },
    };

    private static AuthorRole Role(params (string Kind, string Action)[] perms)
    {
        var dict = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var (kind, action) in perms)
        {
            dict[kind] = dict.TryGetValue(kind, out var existing)
                ? existing.Append(action).ToList()
                : new List<string> { action };
        }
        return new AuthorRole { Permissions = dict };
    }

    private static AgentPrincipal Principal(params string[] scopes) => new("alice", TenantId: null, Scopes: scopes);

    private static async Task<PolicyDecision> Decide(
        AuthorRolesPolicy policy, AgentPrincipal? principal, PolicyOperation op)
        => await new AuthorRolesPolicyEngine(policy).EvaluateAsync(op, manifest: null, principal);

    [Fact]
    public async Task Author_Scope_Can_Write_And_Delete_Its_Kind()
    {
        var p = Principal("vais.author");
        (await Decide(Policy(), p, PolicyOperation.Create)).IsAllowed.Should().BeTrue();
        (await Decide(Policy(), p, PolicyOperation.Update)).IsAllowed.Should().BeTrue();
        (await Decide(Policy(), p, PolicyOperation.Evict)).IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Author_Scope_Limited_To_Agent_Denies_Other_Kinds()
    {
        var decision = await Decide(Policy(), Principal("vais.author"), PolicyOperation.McpGatewayConfigCreate);
        decision.IsAllowed.Should().BeFalse();
        decision.Reason.Should().Contain("McpGatewayConfig");
    }

    [Fact]
    public async Task ReadOnly_Scope_Denies_All_Mutations()
    {
        var p = Principal("vais.readonly");
        (await Decide(Policy(), p, PolicyOperation.Create)).IsAllowed.Should().BeFalse();
        (await Decide(Policy(), p, PolicyOperation.GraphCreate)).IsAllowed.Should().BeFalse();
        (await Decide(Policy(), p, PolicyOperation.EvalSuiteUpsert)).IsAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task Unknown_Scope_Denies()
        => (await Decide(Policy(), Principal("some.other.scope"), PolicyOperation.Create))
            .IsAllowed.Should().BeFalse();

    [Fact]
    public async Task Null_Scopes_Deny_Mutations()
    {
        var p = new AgentPrincipal("bob", TenantId: null, Scopes: null);
        (await Decide(Policy(), p, PolicyOperation.Create)).IsAllowed.Should().BeFalse();
        (await Decide(Policy(), principal: null, PolicyOperation.Create)).IsAllowed.Should().BeFalse();
    }

    [Fact]
    public async Task Write_Permission_Does_Not_Grant_Delete()
    {
        var p = Principal("agent-writer");
        (await Decide(Policy(), p, PolicyOperation.Create)).IsAllowed.Should().BeTrue();
        (await Decide(Policy(), p, PolicyOperation.Update)).IsAllowed.Should().BeTrue();
        (await Decide(Policy(), p, PolicyOperation.Evict)).IsAllowed.Should().BeFalse("delete is not in the granted actions");
    }

    [Theory]
    [InlineData(PolicyOperation.Invoke)]
    [InlineData(PolicyOperation.Signal)]
    [InlineData(PolicyOperation.Query)]
    [InlineData(PolicyOperation.Cancel)]
    [InlineData(PolicyOperation.GraphInvoke)]
    [InlineData(PolicyOperation.GraphResume)]
    [InlineData(PolicyOperation.EvalSuiteQuery)]
    [InlineData(PolicyOperation.McpServerQuery)]
    public async Task NonAuthoring_Verbs_Always_Allowed(PolicyOperation op)
    {
        // No scopes at all — runtime / read verbs must still pass so wiring RBAC
        // never breaks agent invocation or status reads.
        var p = new AgentPrincipal("bob", TenantId: null, Scopes: null);
        (await Decide(Policy(), p, op)).IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Empty_Policy_Denies_Mutations_But_Allows_Runtime()
    {
        var p = Principal("vais.author");
        (await Decide(AuthorRolesPolicy.Empty, p, PolicyOperation.Create)).IsAllowed.Should().BeFalse();
        (await Decide(AuthorRolesPolicy.Empty, p, PolicyOperation.Invoke)).IsAllowed.Should().BeTrue();
    }
}
