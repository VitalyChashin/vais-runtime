// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents.Runtime.Instantiation;

/// <summary>
/// Creates a guardrail instance from a manifest <c>GuardrailRef</c>. Keyed on
/// <c>(Name, Layer)</c> — the same Name can register twice (once for Input,
/// once for Output) when a regex guardrail covers both directions.
/// </summary>
/// <remarks>
/// <para>
/// The returned object is one of <c>IInputGuardrail</c>, <c>IOutputGuardrail</c>,
/// or <c>IToolGuardrail</c> depending on <see cref="Layer"/>. The translator
/// casts and places it in the matching <c>StatefulAgentOptions</c> slot.
/// </para>
/// <para>
/// Consumers register via
/// <c>services.AddSingleton&lt;IGuardrailFactory, MyGuardrailFactory&gt;()</c>.
/// The built-in set (LengthCap / RegexAllowlist / RegexDenylist / LlmAsJudge)
/// ships in Core via <c>AddBuiltinGuardrails</c> (PR 2).
/// </para>
/// </remarks>
public interface IGuardrailFactory
{
    /// <summary>Guardrail name — matched case-insensitive against <c>GuardrailRef.Name</c>.</summary>
    string Name { get; }

    /// <summary>Layer this factory produces — Input, Output, or Tool.</summary>
    GuardrailLayer Layer { get; }

    /// <summary>
    /// Build the guardrail. Cast the return value to <c>IInputGuardrail</c>,
    /// <c>IOutputGuardrail</c>, or <c>IToolGuardrail</c> based on
    /// <see cref="Layer"/>.
    /// </summary>
    /// <param name="parameters">
    /// Raw <c>GuardrailRef.Params</c> as a <see cref="JsonElement"/>. May be
    /// <c>null</c>; factories that require params should throw
    /// <see cref="ManifestInstantiationException"/> with
    /// <see cref="ManifestInstantiationUrns.GuardrailParamsInvalid"/>.
    /// </param>
    /// <param name="serviceProvider">For factories that need DI-resolved collaborators (e.g. LLM-as-judge pulling an <c>ICompletionProvider</c> via <see cref="ICompletionProviderPool"/>).</param>
    /// <exception cref="ManifestInstantiationException">On malformed or missing params.</exception>
    object Create(JsonElement? parameters, IServiceProvider serviceProvider);
}
