// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Vais.Agents.Control.Manifests;
using Xunit;

namespace Vais.Agents.Control.Http.Tests;

/// <summary>
/// Round-trip completeness guard for <see cref="EvalSuiteManifest"/> — every optional
/// field (incl. the nested <c>EvalSuiteSpec</c> → target / defaults / baseline / cases /
/// sampling / assertions tree) must survive <c>EnvelopeSerializer.Serialize</c> →
/// <see cref="JsonAgentGraphManifestLoader"/>.
/// </summary>
/// <remarks>
/// EvalSuite has mutually-exclusive shapes (Cases XOR Sampling; AgentId XOR GraphId), so
/// rows carry per-shape fixtures. This guard surfaced (and locks) a real apply-side drop:
/// the serializer emitted neither <c>sampling</c> nor spec-level <c>assertions</c>, so a
/// continuous-mode suite lost its config on <c>vais apply</c>. Enum fields
/// (<c>ReplayMode</c>, case <c>Replay</c>) are walker-skipped.
/// </remarks>
public sealed class EvalSuiteManifestFieldRoundTripTests
{
    private static EvalSuiteManifest Suite(EvalSuiteSpec spec, string? description = null,
        IReadOnlyDictionary<string, string>? labels = null)
        => new("eval-x", "1.0", description, labels) { Spec = spec };

    private static EvalCase BasicCase() => new() { Id = "case-1", Input = "hello" };

    private static EvalCase RichCase() => new()
    {
        Id = "case-1",
        Name = "case name",
        Description = "case desc",
        Input = "hello",
        ExpectedOutput = "expected out",
        Variables = new Dictionary<string, JsonElement> { ["key"] = JsonDocument.Parse("\"val\"").RootElement.Clone() },
        InitialHistory = new[] { new EvalHistoryTurn("user", "hi") },
        Assertions = new[] { new EvalAssertion("contains", JsonDocument.Parse("{\"value\":\"x\"}").RootElement.Clone()) },
    };

    private static EvalSuiteManifest CasesRich() => Suite(
        new EvalSuiteSpec
        {
            AgentId = "my-agent",
            Defaults = new EvalDefaults { JudgeModel = "judge-x", Timeout = TimeSpan.FromSeconds(30) },
            Baseline = new EvalBaseline("run-123"),
            Cases = new[] { RichCase() },
        },
        description: "a suite",
        labels: new Dictionary<string, string> { ["team"] = "platform" });

    private static EvalSuiteManifest GraphSuite() =>
        Suite(new EvalSuiteSpec { GraphId = "my-graph", Cases = new[] { BasicCase() } });

    private static EvalSuiteManifest TargetSuite() =>
        Suite(new EvalSuiteSpec { Target = new EvalTarget { AgentRef = "ta", AgentVersion = "2.0" }, Cases = new[] { BasicCase() } });

    private static EvalSuiteManifest TargetGraphSuite() =>
        Suite(new EvalSuiteSpec { Target = new EvalTarget { GraphRef = "tg" }, Cases = new[] { BasicCase() } });

    private static EvalSuiteManifest SamplingRich() => Suite(new EvalSuiteSpec
    {
        AgentId = "my-agent",
        Sampling = new EvalSamplingSpec { Rate = 0.25, MinPerHour = 10, WindowDuration = TimeSpan.FromMinutes(30) },
        Assertions = new[] { new EvalAssertion("regex", JsonDocument.Parse("{\"pattern\":\"p\"}").RootElement.Clone()) },
    });

    private static object?[] Row(string path, EvalSuiteManifest manifest, Func<EvalSuiteManifest, object?> extract, object? expected)
        => [path, manifest, extract, expected];

    public static IEnumerable<object?[]> RoundTripCases()
    {
        yield return Row("Description", CasesRich(), m => m.Description, "a suite");
        yield return Row("Labels", CasesRich(), m => m.Labels!["team"], "platform");
        yield return Row("Spec.AgentId", CasesRich(), m => m.Spec.AgentId, "my-agent");
        yield return Row("Spec.GraphId", GraphSuite(), m => m.Spec.GraphId, "my-graph");
        yield return Row("Spec.Target.AgentRef", TargetSuite(), m => m.Spec.Target!.AgentRef, "ta");
        yield return Row("Spec.Target.AgentVersion", TargetSuite(), m => m.Spec.Target!.AgentVersion, "2.0");
        yield return Row("Spec.Target.GraphRef", TargetGraphSuite(), m => m.Spec.Target!.GraphRef, "tg");
        yield return Row("Spec.Defaults.JudgeModel", CasesRich(), m => m.Spec.Defaults!.JudgeModel, "judge-x");
        yield return Row("Spec.Defaults.Timeout", CasesRich(), m => m.Spec.Defaults!.Timeout, (object?)TimeSpan.FromSeconds(30));
        yield return Row("Spec.Baseline.RunId", CasesRich(), m => m.Spec.Baseline!.RunId, "run-123");

        yield return Row("Spec.Cases.Id", CasesRich(), m => m.Spec.Cases!.Single().Id, "case-1");
        yield return Row("Spec.Cases.Name", CasesRich(), m => m.Spec.Cases!.Single().Name, "case name");
        yield return Row("Spec.Cases.Description", CasesRich(), m => m.Spec.Cases!.Single().Description, "case desc");
        yield return Row("Spec.Cases.Input", CasesRich(), m => m.Spec.Cases!.Single().Input, "hello");
        yield return Row("Spec.Cases.Variables", CasesRich(), m => m.Spec.Cases!.Single().Variables!["key"].GetString(), "val");
        yield return Row("Spec.Cases.ExpectedOutput", CasesRich(), m => m.Spec.Cases!.Single().ExpectedOutput, "expected out");
        yield return Row("Spec.Cases.InitialHistory.Role", CasesRich(), m => m.Spec.Cases!.Single().InitialHistory!.Single().Role, "user");
        yield return Row("Spec.Cases.InitialHistory.Content", CasesRich(), m => m.Spec.Cases!.Single().InitialHistory!.Single().Content, "hi");
        yield return Row("Spec.Cases.Assertions.Kind", CasesRich(), m => m.Spec.Cases!.Single().Assertions.Single().Kind, "contains");
        yield return Row("Spec.Cases.Assertions.Params", CasesRich(), m => m.Spec.Cases!.Single().Assertions.Single().Params!.Value.GetProperty("value").GetString(), "x");

        yield return Row("Spec.Sampling.Rate", SamplingRich(), m => m.Spec.Sampling!.Rate, (object?)0.25);
        yield return Row("Spec.Sampling.MinPerHour", SamplingRich(), m => m.Spec.Sampling!.MinPerHour, (object?)10);
        yield return Row("Spec.Sampling.WindowDuration", SamplingRich(), m => m.Spec.Sampling!.WindowDuration, (object?)TimeSpan.FromMinutes(30));
        yield return Row("Spec.Assertions.Kind", SamplingRich(), m => m.Spec.Assertions!.Single().Kind, "regex");
        yield return Row("Spec.Assertions.Params", SamplingRich(), m => m.Spec.Assertions!.Single().Params!.Value.GetProperty("pattern").GetString(), "p");
    }

    [Theory]
    [MemberData(nameof(RoundTripCases), DisableDiscoveryEnumeration = true)]
    public async Task Field_RoundTrips(string path, EvalSuiteManifest input, Func<EvalSuiteManifest, object?> extract, object? expected)
    {
        var json = EnvelopeSerializer.Serialize(input);
        var resources = await new JsonAgentGraphManifestLoader().LoadAllResourcesFromStringAsync(json);
        var suite = ((ManifestResource.EvalSuiteCase)resources.Single()).Suite;
        extract(suite).Should().Be(expected,
            because: $"{path} must survive the EvalSuite EnvelopeSerializer → JsonAgentGraphManifestLoader round-trip");
    }

    [Fact]
    public void AllEvalSuiteFields_AreCovered()
    {
        var covered = new HashSet<string>(RoundTripCases().Select(r => (string)r[0]!), StringComparer.Ordinal);
        var discovered = ManifestRoundTripWalker.Discover(typeof(EvalSuiteManifest),
            new HashSet<string>(StringComparer.Ordinal) { "Id", "Version" });
        discovered.Distinct().Except(covered).OrderBy(p => p).Should().BeEmpty(
            because: "every optional field on EvalSuiteManifest must have a round-trip case");
    }
}
