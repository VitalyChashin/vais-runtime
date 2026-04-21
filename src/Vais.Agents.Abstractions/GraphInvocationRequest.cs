// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents;

/// <summary>
/// Input payload for a new graph run. State travels as a schema-bag so the HTTP
/// wire format is JSON-schema-validated without requiring a concrete POCO on the
/// control-plane side; strongly-typed runtimes deserialize on entry to the graph.
/// </summary>
/// <param name="InitialState">
/// Starting state for the graph run. Keys must conform to the manifest's
/// <c>stateSchema</c> when a schema is present; the orchestrator validates before
/// the entry node executes.
/// </param>
/// <param name="Metadata">
/// Caller-supplied key/value pairs propagated into <see cref="AgentContext"/>
/// for the duration of the run.
/// </param>
/// <param name="RunId">
/// Optional caller-supplied run identifier. When provided the control plane uses it
/// as-is (idempotent re-delivery); when <see langword="null"/> a new GUID is generated.
/// </param>
/// <param name="MaxSteps">
/// Override for the manifest's <c>maxSteps</c> limit. <see langword="null"/> means
/// use the manifest default.
/// </param>
public sealed record GraphInvocationRequest(
    IDictionary<string, JsonElement> InitialState,
    IReadOnlyDictionary<string, string>? Metadata = null,
    string? RunId = null,
    int? MaxSteps = null);
