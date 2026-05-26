// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Vais.Agents.Control.Manifests;
using Xunit;

namespace Vais.Agents.Core.Tests.Ontology;

/// <summary>
/// C1-11 verify gate: request-phase semantic argument validation (short-circuit with
/// {ok:false, reason, suggestions[]} on missing cross-refs) + response-phase enrichment
/// (inject _ontology metadata).
/// </summary>
public sealed class DomainOntologyCallMiddlewareTests
{
    private static readonly DomainOntologyArtifact Artifact = new()
    {
        OntologyVersion = "v1",
        Tools = new Dictionary<string, DomainConcept>
        {
            ["fetch_url"] = new()
            {
                Description = "Fetch a URL.",
                Tags = ["risk:network", "risk:Destructive"],
                CrossRefs = [new DomainCrossRef("url", "Url", "one")],
            },
            ["query_database"] = new()
            {
                CrossRefs =
                [
                    new DomainCrossRef("connection", "Database", "one"),
                    new DomainCrossRef("sql", "Query", "one"),
                ],
            },
            ["no_xrefs"] = new() { Tags = ["category:harmless"] },
        },
    };

    private static IDomainOntologyCatalog NewCatalog()
        => new DomainOntologyCatalog(Artifact);

    // ── argument validation (request-phase) ───────────────────────────────────

    [Fact]
    public async Task ArgValidation_ShortCircuitsWhenCrossRefArgIsMissing()
    {
        var mw = new DomainOntologyArgValidationMiddleware(NewCatalog());
        var ctx = NewContext("fetch_url", """{"other":"value"}""");
        var nextCalled = false;

        var outcome = await mw.InvokeAsync(ctx, () => { nextCalled = true; return Task.FromResult(new ToolCallOutcome("c1", "upstream")); });

        nextCalled.Should().BeFalse("missing cross-ref args short-circuit before upstream dispatch");
        var payload = JsonDocument.Parse(outcome.Result!);
        payload.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        payload.RootElement.GetProperty("reason").GetString().Should().Contain("url");
        payload.RootElement.GetProperty("suggestions").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ArgValidation_ShortCircuitsWhenCrossRefArgIsEmptyString()
    {
        var mw = new DomainOntologyArgValidationMiddleware(NewCatalog());
        var ctx = NewContext("fetch_url", """{"url":""}""");

        var outcome = await mw.InvokeAsync(ctx, () => Task.FromResult(new ToolCallOutcome("c1", "upstream")));

        JsonDocument.Parse(outcome.Result!).RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task ArgValidation_AllCrossRefArgsMissing_ReportsAllInSuggestions()
    {
        var mw = new DomainOntologyArgValidationMiddleware(NewCatalog());
        var ctx = NewContext("query_database", "{}");

        var outcome = await mw.InvokeAsync(ctx, () => Task.FromResult(new ToolCallOutcome("c1", "upstream")));

        var root = JsonDocument.Parse(outcome.Result!).RootElement;
        root.GetProperty("reason").GetString().Should().Contain("connection").And.Contain("sql");
        root.GetProperty("suggestions").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task ArgValidation_PassesThroughWhenAllCrossRefArgsPresent()
    {
        var mw = new DomainOntologyArgValidationMiddleware(NewCatalog());
        var ctx = NewContext("fetch_url", """{"url":"https://example.com"}""");
        var nextCalled = false;

        var outcome = await mw.InvokeAsync(ctx, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ToolCallOutcome("c1", "fetched body"));
        });

        nextCalled.Should().BeTrue("all cross-refs satisfied — call must reach the upstream");
        outcome.Result.Should().Be("fetched body");
    }

    [Fact]
    public async Task ArgValidation_PassesThroughForToolNotInCatalog()
    {
        var mw = new DomainOntologyArgValidationMiddleware(NewCatalog());
        var ctx = NewContext("unknown_tool", "{}");
        var nextCalled = false;

        var outcome = await mw.InvokeAsync(ctx, () => { nextCalled = true; return Task.FromResult(new ToolCallOutcome("c1", "ok")); });

        nextCalled.Should().BeTrue("unknown tool = passthrough");
        outcome.Result.Should().Be("ok");
    }

    [Fact]
    public async Task ArgValidation_PassesThroughForToolWithoutCrossRefs()
    {
        var mw = new DomainOntologyArgValidationMiddleware(NewCatalog());
        var ctx = NewContext("no_xrefs", "{}");
        var nextCalled = false;

        var outcome = await mw.InvokeAsync(ctx, () => { nextCalled = true; return Task.FromResult(new ToolCallOutcome("c1", "ok")); });

        nextCalled.Should().BeTrue("tool has no cross-refs to validate");
    }

    [Fact]
    public void ArgValidationMiddleware_DeclaresValidationKind()
    {
        var mw = new DomainOntologyArgValidationMiddleware(NewCatalog());
        ((OntologyInterceptor)mw).Kind.Should().Be(InterceptorKind.Validation);
    }

    // ── response enrichment (response-phase) ──────────────────────────────────

    [Fact]
    public async Task ResponseEnrichment_AddsOntologyBlockToJsonResponse()
    {
        var mw = new DomainOntologyResponseEnrichmentMiddleware(NewCatalog());
        var ctx = NewContext("fetch_url", """{"url":"https://example.com"}""");

        var outcome = await mw.InvokeAsync(ctx, () => Task.FromResult(
            new ToolCallOutcome("c1", """{"status":200,"body":"ok"}""")));

        var root = JsonDocument.Parse(outcome.Result!).RootElement;
        root.GetProperty("status").GetInt32().Should().Be(200);
        root.GetProperty("_ontology").GetProperty("tags").GetArrayLength().Should().Be(2);
        root.GetProperty("_ontology").GetProperty("ontologyVersion").GetString().Should().Be("v1");
    }

    [Fact]
    public async Task ResponseEnrichment_PreservesNonJsonResponse()
    {
        var mw = new DomainOntologyResponseEnrichmentMiddleware(NewCatalog());
        var ctx = NewContext("fetch_url", """{"url":"x"}""");

        var outcome = await mw.InvokeAsync(ctx, () => Task.FromResult(new ToolCallOutcome("c1", "just plain text")));

        outcome.Result.Should().Be("just plain text", "non-JSON outputs pass through unchanged");
    }

    [Fact]
    public async Task ResponseEnrichment_PreservesErrorOutcome()
    {
        var mw = new DomainOntologyResponseEnrichmentMiddleware(NewCatalog());
        var ctx = NewContext("fetch_url", """{"url":"x"}""");

        var outcome = await mw.InvokeAsync(ctx, () => Task.FromResult(
            new ToolCallOutcome("c1", "upstream failure msg", Error: "HttpRequestException")));

        outcome.Result.Should().Be("upstream failure msg");
        outcome.Error.Should().Be("HttpRequestException");
    }

    [Fact]
    public async Task ResponseEnrichment_PassesThroughForToolWithoutTags()
    {
        var mw = new DomainOntologyResponseEnrichmentMiddleware(NewCatalog());
        var ctx = NewContext("query_database", """{"connection":"db","sql":"select 1"}""");

        var outcome = await mw.InvokeAsync(ctx, () => Task.FromResult(new ToolCallOutcome("c1", """{"rows":1}""")));

        JsonDocument.Parse(outcome.Result!).RootElement.TryGetProperty("_ontology", out _)
            .Should().BeFalse("no tags ⇒ no enrichment block");
    }

    [Fact]
    public async Task ResponseEnrichment_PassesThroughForToolNotInCatalog()
    {
        var mw = new DomainOntologyResponseEnrichmentMiddleware(NewCatalog());
        var ctx = NewContext("unknown_tool", "{}");

        var outcome = await mw.InvokeAsync(ctx, () => Task.FromResult(new ToolCallOutcome("c1", """{"x":1}""")));

        JsonDocument.Parse(outcome.Result!).RootElement.TryGetProperty("_ontology", out _).Should().BeFalse();
    }

    [Fact]
    public void ResponseEnrichmentMiddleware_DeclaresMutationKind()
    {
        var mw = new DomainOntologyResponseEnrichmentMiddleware(NewCatalog());
        ((OntologyInterceptor)mw).Kind.Should().Be(InterceptorKind.Mutation);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static ToolGatewayContext NewContext(string toolName, string argsJson)
    {
        using var doc = JsonDocument.Parse(argsJson);
        return new ToolGatewayContext(
            ToolName: toolName,
            CallId: "c1",
            Arguments: doc.RootElement.Clone(),
            AgentContext: AgentContext.Empty);
    }
}
