// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Reflection;
using FluentAssertions;
using Vais.Agents.Control.Manifests;
using Xunit;

namespace Vais.Agents.Control.Http.Tests;

/// <summary>
/// M1.1 blanket guard — every non-default enum value on every enum-typed property
/// reachable from <see cref="AgentManifest"/> must round-trip through
/// <see cref="EnvelopeSerializer.Serialize(AgentManifest)"/> → <see cref="JsonAgentManifestLoader.LoadFromStringAsync"/>
/// without being dropped or silently coerced back to its default.
/// </summary>
/// <remarks>
/// Two guards work together:
/// <list type="bullet">
///   <item><see cref="EnumProperty_RoundTrips"/> — one case per (property path,
///   non-default enum value); fails when the serializer or loader loses an enum value.</item>
///   <item><see cref="AllManifestEnumProperties_AreCovered"/> — reflection walker that
///   fails if a new enum-typed property is added to the manifest hierarchy without a
///   corresponding test case in <see cref="RoundTripCases"/>.</item>
/// </list>
/// </remarks>
public sealed class ManifestEnumRoundTripTests
{
    private static AgentManifest Base() => new(
        "test-agent", "1.0",
        new AgentHandlerRef("declarative"),
        Array.Empty<ProtocolBinding>(),
        Array.Empty<ToolRef>());

    // ── covered paths — must stay in sync with RoundTripCases() ──────────────

    private static readonly IReadOnlySet<string> CoveredPaths = new HashSet<string>(StringComparer.Ordinal)
    {
        "AgentMode",
        "Reasoning.Pattern",
        "LocalAgents[].Mode",
    };

    // ── round-trip test cases ─────────────────────────────────────────────────

    /// <summary>
    /// Returns one row per (property-path, non-default enum value):
    /// [ path, AgentManifest, Func&lt;AgentManifest, object?&gt;, expected ].
    /// </summary>
    public static IEnumerable<object[]> RoundTripCases()
    {
        // AgentMode — serialiser special-cases non-default values via .ToString();
        // loader reads as string. Any new AgentMode value auto-generates a case here.
        foreach (var val in Enum.GetValues<AgentMode>().Where(v => v != default))
            yield return new object[]
            {
                "AgentMode",
                Base() with { AgentMode = val },
                (Func<AgentManifest, object?>)(m => m.AgentMode),
                (object)val,
            };

        // ReasoningSpec.Pattern — AddIfSet serialises via JsonOptions;
        // [JsonConverter(typeof(JsonStringEnumConverter))] on ReasoningPattern is required.
        foreach (var val in Enum.GetValues<ReasoningPattern>().Where(v => v != default))
            yield return new object[]
            {
                "Reasoning.Pattern",
                Base() with { Reasoning = new ReasoningSpec(val, SchemaRef: "test://schema") },
                (Func<AgentManifest, object?>)(m => m.Reasoning!.Pattern),
                (object)val,
            };

        // LocalAgentRef.Mode — serialised via JsonOptions;
        // [JsonConverter(typeof(JsonStringEnumConverter))] on LocalAgentInvocationMode.
        foreach (var val in Enum.GetValues<LocalAgentInvocationMode>().Where(v => v != default))
            yield return new object[]
            {
                "LocalAgents[].Mode",
                Base() with { LocalAgents = new[] { new LocalAgentRef("sub-agent", Mode: val) } },
                (Func<AgentManifest, object?>)(m => m.LocalAgents!.Single().Mode),
                (object)val,
            };
    }

    [Theory]
    [MemberData(nameof(RoundTripCases), DisableDiscoveryEnumeration = true)]
    public async Task EnumProperty_RoundTrips(
        string path, AgentManifest input, Func<AgentManifest, object?> extract, object expected)
    {
        var json = EnvelopeSerializer.Serialize(input);
        var manifests = await new JsonAgentManifestLoader().LoadFromStringAsync(json);
        extract(manifests.Single()).Should().Be(expected,
            because: $"{path} must survive the EnvelopeSerializer → JsonAgentManifestLoader round-trip");
    }

    // ── coverage guard ────────────────────────────────────────────────────────

    [Fact]
    public void AllManifestEnumProperties_AreCovered()
    {
        var discovered = DiscoverEnumPropertyPaths(typeof(AgentManifest));
        var uncovered = discovered.Except(CoveredPaths).OrderBy(p => p).ToList();
        uncovered.Should().BeEmpty(
            because: "every enum-typed property reachable from AgentManifest must have a round-trip " +
                     $"test case; add the missing paths to {nameof(CoveredPaths)} and {nameof(RoundTripCases)}()");
    }

    // ── reflection walker ─────────────────────────────────────────────────────

    private static IReadOnlyList<string> DiscoverEnumPropertyPaths(Type rootType)
    {
        var results = new List<string>();
        WalkType(rootType, "", new HashSet<Type>(), results);
        return results;
    }

    private static void WalkType(Type type, string prefix, HashSet<Type> visited, List<string> results)
    {
        if (!visited.Add(type)) return;

        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var path = prefix.Length == 0 ? prop.Name : $"{prefix}.{prop.Name}";
            var underlying = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;

            if (underlying.IsEnum)
                results.Add(path);
            else if (IsManifestRecordType(underlying))
                WalkType(underlying, path, visited, results);
            else if (GetListElementType(prop.PropertyType) is { } elemType && IsManifestRecordType(elemType))
                WalkType(elemType, $"{path}[]", visited, results);
        }
    }

    private static bool IsManifestRecordType(Type t)
        => t.Assembly == typeof(AgentManifest).Assembly
           && !t.IsEnum
           && !t.IsPrimitive
           && t != typeof(string);

    private static Type? GetListElementType(Type type)
    {
        if (type.IsArray) return type.GetElementType();
        if (type.IsGenericType)
        {
            var def = type.GetGenericTypeDefinition();
            if (def == typeof(IReadOnlyList<>)
                || def == typeof(IEnumerable<>)
                || def == typeof(List<>))
                return type.GetGenericArguments()[0];
        }
        return null;
    }
}
