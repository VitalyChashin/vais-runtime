// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents;

/// <summary>
/// Declarative guardrail bindings — three parallel lists matching the three-layer
/// split shipped in v0.4 (<see cref="IInputGuardrail"/> / <see cref="IOutputGuardrail"/>
/// / <see cref="IToolGuardrail"/>). Each entry names a guardrail registered in the
/// host's DI keyspace plus optional guardrail-specific parameters.
/// </summary>
/// <param name="Input">Guardrails that run before the provider call.</param>
/// <param name="Output">Guardrails that run on the provider response.</param>
/// <param name="Tool">Guardrails that run around each tool-call dispatch.</param>
public sealed record GuardrailsSpec(
    IReadOnlyList<GuardrailRef>? Input = null,
    IReadOnlyList<GuardrailRef>? Output = null,
    IReadOnlyList<GuardrailRef>? Tool = null);

/// <summary>
/// Reference to a single guardrail by name, with optional parameters. Params are
/// opaque JSON — the runtime hands them to the guardrail's factory to parse.
/// </summary>
/// <param name="Name">Guardrail name — matches a DI key.</param>
/// <param name="Params">Guardrail-specific configuration. Shape is guardrail-defined.</param>
public sealed record GuardrailRef(string Name, JsonElement? Params = null);
