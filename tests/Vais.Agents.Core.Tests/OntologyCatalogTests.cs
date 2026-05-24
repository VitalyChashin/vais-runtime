// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Control.Manifests;
using Xunit;

namespace Vais.Agents.Core.Tests;

/// <summary>
/// ND-4 guard — <see cref="OntologyCatalog"/> merges the base ontology with an overlay
/// deterministically. Base-only mode works when no overlay is supplied. Unknown kind
/// produces a clear error.
/// </summary>
public sealed class OntologyCatalogTests
{
    private static string BaseOntologyJson() =>
        File.ReadAllText(Path.Combine(RepoContracts.Dir(), "ontology", "base-ontology.json"));

    [Fact]
    public void Build_BaseOnly_ContainsAll7Kinds()
    {
        var catalog = OntologyCatalog.Build(BaseOntologyJson());
        foreach (var kind in new[] { "Agent", "AgentGraph", "McpServer", "LlmGatewayConfig", "McpGatewayConfig", "ContainerPlugin", "EvalSuite" })
            catalog.TryGet(kind, out _).Should().BeTrue($"kind '{kind}' must be present in base-only mode");
    }

    [Fact]
    public void Build_BaseOnly_OntologyVersionIsPresent()
    {
        var catalog = OntologyCatalog.Build(BaseOntologyJson());
        catalog.OntologyVersion.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Build_BaseOnly_AgentHasCrossRefs()
    {
        var catalog = OntologyCatalog.Build(BaseOntologyJson());
        var entry = catalog.Get("Agent");
        var fields = entry.CrossRefs.Select(r => r.FieldPath).ToList();
        fields.Should().Contain("llmGatewayRef");
        fields.Should().Contain("mcpGatewayRef");
    }

    [Fact]
    public void Build_BaseOnly_AgentHasRequiredFields()
    {
        var catalog = OntologyCatalog.Build(BaseOntologyJson());
        var entry = catalog.Get("Agent");
        entry.RequiredFields.Should().Contain("handler");
        entry.RequiredFields.Should().Contain("protocols");
    }

    [Fact]
    public void Build_BaseOnly_McpServerHasCrossRefs()
    {
        var catalog = OntologyCatalog.Build(BaseOntologyJson());
        var entry = catalog.Get("McpServer");
        var targets = entry.CrossRefs.Select(r => r.TargetKind).ToList();
        targets.Should().Contain("McpGatewayConfig");
        targets.Should().Contain("McpServer");
    }

    [Fact]
    public void Build_WithOverlay_TagsMerged()
    {
        var overlay = OntologyOverlayLoader.LoadFromJson("""
            { "kinds": { "ContainerPlugin": { "tags": ["risk:RunsCode"] } } }
            """);
        var catalog = OntologyCatalog.Build(BaseOntologyJson(), overlay);
        catalog.Get("ContainerPlugin").Tags.Should().Contain("risk:RunsCode");
    }

    [Fact]
    public void Build_WithOverlay_DescriptionOverrides()
    {
        var overlay = OntologyOverlayLoader.LoadFromJson("""
            { "kinds": { "Agent": { "description": "Override description." } } }
            """);
        var catalog = OntologyCatalog.Build(BaseOntologyJson(), overlay);
        catalog.Get("Agent").Description.Should().Be("Override description.");
    }

    [Fact]
    public void Build_WithOverlay_ManualConceptKindAdded()
    {
        var overlay = OntologyOverlayLoader.LoadFromJson("""
            { "kinds": { "Extension": { "manualConcept": "In-process extension handler." } } }
            """);
        var catalog = OntologyCatalog.Build(BaseOntologyJson(), overlay);
        catalog.TryGet("Extension", out var entry).Should().BeTrue("Extension must appear via manual concept");
        entry.ManualConcept.Should().Contain("extension handler");
    }

    [Fact]
    public void Build_WithNullOverlay_SameAsBaseOnly()
    {
        var a = OntologyCatalog.Build(BaseOntologyJson(), null);
        var b = OntologyCatalog.Build(BaseOntologyJson());
        a.Kinds.Should().BeEquivalentTo(b.Kinds);
        a.OntologyVersion.Should().Be(b.OntologyVersion);
    }

    [Fact]
    public void Get_UnknownKind_Throws()
    {
        var catalog = OntologyCatalog.Build(BaseOntologyJson());
        var act = () => catalog.Get("NoSuchKind");
        act.Should().Throw<KeyNotFoundException>();
    }

    [Fact]
    public void Build_WithOverlay_RecipesFilteredByKind()
    {
        var overlay = OntologyOverlayLoader.LoadFromJson("""
            {
              "recipes": [{
                "name": "gw-first",
                "steps": [
                  { "kind": "LlmGatewayConfig", "action": "apply" },
                  { "kind": "Agent", "action": "apply" }
                ]
              }]
            }
            """);
        var catalog = OntologyCatalog.Build(BaseOntologyJson(), overlay);
        // Agent entry should see the recipe; McpServer should not.
        catalog.Get("Agent").Recipes.Should().Contain(r => r.Name == "gw-first");
        catalog.Get("McpServer").Recipes.Should().NotContain(r => r.Name == "gw-first");
    }

    [Fact]
    public void Build_GlobalRecipes_AreExposedOnCatalog()
    {
        var overlay = OntologyOverlayLoader.LoadFromJson("""
            {
              "recipes": [{ "name": "my-recipe", "steps": [{ "kind": "Agent" }] }]
            }
            """);
        var catalog = OntologyCatalog.Build(BaseOntologyJson(), overlay);
        catalog.Recipes.Should().Contain(r => r.Name == "my-recipe");
    }
}
