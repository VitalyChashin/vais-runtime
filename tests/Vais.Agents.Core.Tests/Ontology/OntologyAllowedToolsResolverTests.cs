// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Control.Manifests;
using Xunit;

namespace Vais.Agents.Core.Tests.Ontology;

/// <summary>
/// C2-4 verify gate: ontology-driven AllowedTools computation. Tag-intersection policy —
/// untagged sub-agents are open, tagged sub-agents need a caller scope that matches at
/// least one tag, wildcard scope grants all, empty caller scope honors the dev-default flag.
/// </summary>
public sealed class OntologyAllowedToolsResolverTests
{
    private static CapabilityMap Map(params SubAgentCapability[] subs) => new("coord", subs);

    private static SubAgentCapability Sub(string name, params string[] tags) =>
        new(name, name + "-agent", $"{name} description", tags, LocalAgentInvocationMode.Blocking);

    [Fact]
    public void UntaggedSubAgentIsAlwaysAllowed_OpenByDefault()
    {
        var resolver = new OntologyAllowedToolsResolver();
        var allowed = resolver.Compute(callerScopes: ["role:reviewer"],
            Map(Sub("plain"), Sub("tagged", "role:tester")));

        allowed.Should().Contain("plain", "untagged sub-agents are open — deployer tags to restrict");
        allowed.Should().NotContain("tagged");
    }

    [Fact]
    public void TaggedSubAgentAllowedWhenCallerScopeMatchesAnyTag()
    {
        var resolver = new OntologyAllowedToolsResolver();
        var allowed = resolver.Compute(["role:reviewer", "team:platform"],
            Map(Sub("reviewer", "role:reviewer"), Sub("deployer", "role:ops")));

        allowed.Should().Contain("reviewer");
        allowed.Should().NotContain("deployer");
    }

    [Fact]
    public void TaggedSubAgentDeniedWhenNoScopeMatches()
    {
        var resolver = new OntologyAllowedToolsResolver();
        var allowed = resolver.Compute(["role:visitor"],
            Map(Sub("reviewer", "role:reviewer")));

        allowed.Should().BeEmpty();
    }

    [Fact]
    public void WildcardScopeGrantsAllSubAgents()
    {
        var resolver = new OntologyAllowedToolsResolver();
        var allowed = resolver.Compute(["*"],
            Map(Sub("reviewer", "role:reviewer"), Sub("deployer", "role:ops")));

        allowed.Should().BeEquivalentTo(["reviewer", "deployer"]);
    }

    [Fact]
    public void EmptyCallerScopes_GrantedByDefault_DevPosture()
    {
        var resolver = new OntologyAllowedToolsResolver();
        var allowed = resolver.Compute(callerScopes: [],
            Map(Sub("reviewer", "role:reviewer"), Sub("deployer", "role:ops")));

        allowed.Should().BeEquivalentTo(["reviewer", "deployer"],
            "dev posture default — empty caller scopes ⇒ all sub-agents allowed (mirrors AgentContext.AllowedTools = null)");
    }

    [Fact]
    public void EmptyCallerScopes_StrictMultiTenant_DeniesEveryTaggedSubAgent()
    {
        var resolver = new OntologyAllowedToolsResolver(
            new OntologyAllowedToolsResolverOptions { GrantOnEmptyScope = false });
        var allowed = resolver.Compute(callerScopes: [],
            Map(Sub("reviewer", "role:reviewer"), Sub("plain")));

        allowed.Should().BeEquivalentTo(["plain"],
            "strict mode — empty caller scopes ⇒ tagged sub-agents denied; untagged still open");
    }

    [Fact]
    public void NullCallerScopesTreatedAsEmpty()
    {
        var resolver = new OntologyAllowedToolsResolver();
        var allowed = resolver.Compute(callerScopes: null,
            Map(Sub("reviewer", "role:reviewer"), Sub("plain")));

        allowed.Should().BeEquivalentTo(["plain", "reviewer"]);
    }

    [Fact]
    public void EmptyMapResultsInEmptyAllowedSet()
    {
        var resolver = new OntologyAllowedToolsResolver();
        var allowed = resolver.Compute(["*"], CapabilityMap.Empty("coord"));
        allowed.Should().BeEmpty();
    }

    [Fact]
    public void CustomWildcardScopeIsHonored()
    {
        var resolver = new OntologyAllowedToolsResolver(
            new OntologyAllowedToolsResolverOptions { WildcardScope = "admin:all" });
        var allowed = resolver.Compute(["admin:all"], Map(Sub("any", "role:something")));

        allowed.Should().Contain("any");
    }

    [Fact]
    public void NullCustomWildcardScope_DisablesWildcardHandling()
    {
        var resolver = new OntologyAllowedToolsResolver(
            new OntologyAllowedToolsResolverOptions { WildcardScope = null });
        var allowed = resolver.Compute(["*"], Map(Sub("any", "role:something")));

        allowed.Should().BeEmpty("with WildcardScope disabled, '*' is treated as a literal scope and doesn't match");
    }

    [Fact]
    public void Compute_RejectsNullMap()
    {
        var resolver = new OntologyAllowedToolsResolver();
        FluentActions.Invoking(() => resolver.Compute(["*"], null!)).Should().Throw<ArgumentNullException>();
    }
}
