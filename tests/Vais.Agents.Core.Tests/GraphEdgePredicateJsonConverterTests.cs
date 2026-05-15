// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Vais.Agents.Core.Tests;

/// <summary>
/// Pins the over-the-wire JSON shape emitted and consumed by
/// <see cref="GraphEdgePredicateJsonConverter"/> and <see cref="GraphEdgeEffectJsonConverter"/>.
/// If a future refactor changes the wire shape, at least one test here must fail.
/// </summary>
public sealed class GraphEdgePredicateJsonConverterTests
{
    // ── GraphEdgePredicate serialization ──────────────────────────────────────

    [Fact]
    public void Always_SerializesAsString()
    {
        var json = JsonSerializer.Serialize<GraphEdgePredicate>(new GraphEdgePredicate.Always());

        json.Should().Be("\"always\"");
    }

    [Fact]
    public void Expression_SerializesAsString()
    {
        var json = JsonSerializer.Serialize<GraphEdgePredicate>(new GraphEdgePredicate.Expression("=Local.x"));

        json.Should().Be("\"=Local.x\"");
    }

    [Fact]
    public void PropertyMatcher_SerializesAsObject()
    {
        var value = JsonDocument.Parse("42").RootElement.Clone();
        var json = JsonSerializer.Serialize<GraphEdgePredicate>(
            new GraphEdgePredicate.PropertyMatcher("score", GraphPredicateOperator.Eq, value));

        var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("property").GetString().Should().Be("score");
        doc.RootElement.GetProperty("operator").GetString().Should().Be("Eq");
        doc.RootElement.GetProperty("value").GetInt32().Should().Be(42);
    }

    [Fact]
    public void AllOf_SerializesAsNestedArrayObject()
    {
        var predicate = new GraphEdgePredicate.AllOf(new GraphEdgePredicate[]
        {
            new GraphEdgePredicate.Always(),
            new GraphEdgePredicate.Expression("=Local.x > 0"),
        });
        var json = JsonSerializer.Serialize<GraphEdgePredicate>(predicate);

        var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("allOf", out var arr).Should().BeTrue();
        arr.GetArrayLength().Should().Be(2);
    }

    // ── GraphEdgeEffect serialization ─────────────────────────────────────────

    [Fact]
    public void Increment_SerializesAsObject_WithBy()
    {
        var json = JsonSerializer.Serialize<GraphEdgeEffect>(new GraphEdgeEffect.Increment("retryCount"));

        var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("increment", out var incObj).Should().BeTrue();
        incObj.GetProperty("property").GetString().Should().Be("retryCount");
        incObj.GetProperty("by").GetInt32().Should().Be(1);
    }

    [Fact]
    public void EffectHandlerRef_NullAssemblyName_OmitsField()
    {
        var json = JsonSerializer.Serialize<GraphEdgeEffect>(
            new GraphEdgeEffect.HandlerRef(new GraphHandlerRef("MyEffect")));

        var doc = JsonDocument.Parse(json);
        doc.RootElement.TryGetProperty("handlerRef", out var hrObj).Should().BeTrue();
        hrObj.TryGetProperty("assemblyName", out _).Should().BeFalse();
    }

    // ── GraphEdgePredicate deserialization error cases ────────────────────────

    [Fact]
    public void UnknownStringPredicate_ThrowsJsonException()
    {
        var act = () => JsonSerializer.Deserialize<GraphEdgePredicate>("\"unknownValue\"");

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void EmptyObject_ThrowsJsonException()
    {
        var act = () => JsonSerializer.Deserialize<GraphEdgePredicate>("{}");

        act.Should().Throw<JsonException>();
    }
}
