// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Control.Manifests;
using Xunit;

namespace Vais.Agents.Core.Tests;

/// <summary>
/// ND-3 guard — the overlay format parses correctly, merges deterministically over the base,
/// and a missing or null overlay yields base-only mode without errors.
/// </summary>
public sealed class OntologyOverlayTests
{
    private const string MinimalOverlayJson = """
        {
          "kinds": {
            "ContainerPlugin": {
              "tags": ["risk:RunsCode"],
              "description": "Runs code in a container."
            },
            "Extension": {
              "tags": ["risk:RunsCode"],
              "manualConcept": "In-process extension handler.",
              "description": "Extension handler."
            }
          },
          "recipes": [
            {
              "name": "gateway-before-agent",
              "description": "Apply gateways first.",
              "steps": [
                { "kind": "LlmGatewayConfig", "action": "apply" },
                { "kind": "Agent",            "action": "apply" }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void LoadFromJson_Null_ReturnsEmpty()
        => OntologyOverlayLoader.LoadFromJson(null).Should().BeSameAs(OntologyOverlay.Empty);

    [Fact]
    public void LoadFromJson_Empty_ReturnsEmpty()
        => OntologyOverlayLoader.LoadFromJson("  ").Should().BeSameAs(OntologyOverlay.Empty);

    [Fact]
    public void LoadFromFile_MissingPath_ReturnsEmpty()
        => OntologyOverlayLoader.LoadFromFile("no-such-file.json").Should().BeSameAs(OntologyOverlay.Empty);

    [Fact]
    public void LoadFromJson_Parses_AuthorRoles()
    {
        // NB-4: the overlay carries the deployment-local RBAC map (scope -> per-kind permissions).
        const string json = """
            {
              "authorRoles": {
                "roles": {
                  "vais.author":   { "permissions": { "Agent": ["*"], "EvalSuite": ["write", "delete"] } },
                  "vais.readonly": { "permissions": {} }
                }
              }
            }
            """;

        var overlay = OntologyOverlayLoader.LoadFromJson(json);

        overlay.AuthorRoles.Should().NotBeNull();
        overlay.AuthorRoles!.Roles.Should().ContainKeys("vais.author", "vais.readonly");
        overlay.AuthorRoles.Roles!["vais.author"].Permissions!["Agent"].Should().Contain("*");
        overlay.AuthorRoles.Roles!["vais.author"].Permissions!["EvalSuite"].Should().BeEquivalentTo("write", "delete");
        overlay.AuthorRoles.Roles!["vais.readonly"].Permissions.Should().BeEmpty();
    }

    [Fact]
    public void LoadFromJson_Without_AuthorRoles_LeavesThemNull()
        => OntologyOverlayLoader.LoadFromJson(MinimalOverlayJson).AuthorRoles.Should().BeNull();

    [Fact]
    public void LoadFromFile_NullPath_ReturnsEmpty()
        => OntologyOverlayLoader.LoadFromFile(null).Should().BeSameAs(OntologyOverlay.Empty);

    [Fact]
    public void LoadFromJson_ParsesKindsAndTags()
    {
        var overlay = OntologyOverlayLoader.LoadFromJson(MinimalOverlayJson);
        overlay.Kinds.Should().NotBeNull();
        overlay.Kinds!["ContainerPlugin"].Tags.Should().Contain("risk:RunsCode");
        overlay.Kinds["Extension"].ManualConcept.Should().Contain("extension handler");
    }

    [Fact]
    public void LoadFromJson_ParsesRecipes()
    {
        var overlay = OntologyOverlayLoader.LoadFromJson(MinimalOverlayJson);
        overlay.Recipes.Should().HaveCount(1);
        overlay.Recipes![0].Name.Should().Be("gateway-before-agent");
        overlay.Recipes[0].Steps.Should().HaveCount(2);
        overlay.Recipes[0].Steps[0].Kind.Should().Be("LlmGatewayConfig");
    }

    [Fact]
    public void ForKind_UnknownKind_ReturnsEmptyKindOverlay()
    {
        var overlay = OntologyOverlayLoader.LoadFromJson(MinimalOverlayJson);
        var ko = overlay.ForKind("NonExistentKind");
        ko.Tags.Should().BeNull();
        ko.Description.Should().BeNull();
        ko.ManualConcept.Should().BeNull();
    }

    [Fact]
    public void LoadFromJson_ExampleFile_Parses()
    {
        // The checked-in example overlay must parse without errors.
        var examplePath = Path.Combine(RepoContracts.Dir(), "ontology", "overlay.example.json");
        // Example overlay uses comments — LoadFromJson reads via AllowComments=true.
        var json = File.ReadAllText(examplePath);
        var overlay = OntologyOverlayLoader.LoadFromJson(json);
        overlay.Kinds.Should().NotBeNullOrEmpty("example overlay must define at least one kind");
        overlay.Recipes.Should().NotBeNullOrEmpty("example overlay must define at least one recipe");
    }

    [Fact]
    public void LoadFromJson_RepeatParse_YieldsEqualValues()
    {
        var a = OntologyOverlayLoader.LoadFromJson(MinimalOverlayJson);
        var b = OntologyOverlayLoader.LoadFromJson(MinimalOverlayJson);
        // Both parses must yield the same shape (same recipe count, same tags).
        a.Recipes.Should().HaveCount(b.Recipes?.Count ?? 0);
        a.ForKind("ContainerPlugin").Tags.Should()
            .BeEquivalentTo(b.ForKind("ContainerPlugin").Tags!);
    }
}
