// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vais.Agents;

/// <summary>
/// Schema-Guided Reasoning (SGR) configuration for an agent. Contract-only in v0.6 —
/// shipped so manifests lock wire shape now, execution lands in a later pillar.
/// </summary>
/// <remarks>
/// <para>
/// Exactly one of <see cref="Schema"/> / <see cref="SchemaRef"/> must be set when a
/// reasoning spec is present. The manifest loader enforces the rule. Schema field
/// order is load-bearing under SGR — the YAML / JSON loader preserves insertion
/// order and the runtime passes the schema to the LLM with that order intact, so
/// the LLM fills intermediate "reasoning" fields before conclusions.
/// </para>
/// </remarks>
/// <param name="Pattern">Reasoning flow shape — see <see cref="ReasoningPattern"/>.</param>
/// <param name="Schema">Inline JSON Schema for the reasoning output. Field order matters.</param>
/// <param name="SchemaRef">Alternative to <see cref="Schema"/> — opaque name resolved by the host's DI keyspace.</param>
/// <param name="MaxIterations">Cap on cycle iterations for <see cref="ReasoningPattern.Cycle"/>. Null = runtime default.</param>
/// <param name="MaxClarifications">Cap on clarification rounds — interrupts raised to the user. Null = runtime default.</param>
public sealed record ReasoningSpec(
    ReasoningPattern Pattern,
    JsonElement? Schema = null,
    string? SchemaRef = null,
    int? MaxIterations = null,
    int? MaxClarifications = null);

/// <summary>
/// Schema-flow pattern for <see cref="ReasoningSpec"/>. Cascade is the canonical SGR
/// shape (sequential reasoning steps); routing + cycle cover decision-gated and
/// iterative patterns surveyed during v0.6 scoping.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReasoningPattern
{
    /// <summary>Sequential reasoning steps — fields fill top-to-bottom, each sees prior fields as primed context.</summary>
    Cascade = 0,

    /// <summary>Decision-gated — an <c>enum</c> field selects which subsequent fields are populated (state-machine shape).</summary>
    Routing = 1,

    /// <summary>Iterative refinement — the schema completes repeatedly with a <c>continue_reasoning</c> gate, capped at <see cref="ReasoningSpec.MaxIterations"/>.</summary>
    Cycle = 2,
}
