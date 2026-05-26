// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents;

/// <summary>
/// Outcome categorization for a trajectory event. Coarse-grained on purpose — error-type
/// details live in <see cref="TrajectoryOutcome.ErrorType"/> when relevant, but the kind
/// drives the bulk of induction (success rate per shape, etc.).
/// </summary>
public enum TrajectoryOutcomeKind
{
    /// <summary>The intercepted operation completed normally.</summary>
    Ok = 0,

    /// <summary>The operation threw / returned an error outcome — see <see cref="TrajectoryOutcome.ErrorType"/>.</summary>
    Error = 1,

    /// <summary>A middleware short-circuited the operation (e.g. cartridge denial, depth guard).</summary>
    ShortCircuit = 2,
}

/// <summary>Outcome attached to a <see cref="TrajectoryEvent"/>.</summary>
/// <param name="Kind">Coarse categorization — see <see cref="TrajectoryOutcomeKind"/>.</param>
/// <param name="ErrorType">Exception type name (or short-circuit reason key) when <paramref name="Kind"/> ≠ Ok.</param>
public sealed record TrajectoryOutcome(TrajectoryOutcomeKind Kind, string? ErrorType = null);

/// <summary>
/// One typed trajectory event written to <see cref="IInterceptorTeeStore"/>. The
/// <see cref="ArgumentsShape"/> field is the central guarantee — it captures the
/// *shape* of arguments (name → JSON-value-kind), never raw values, so the store can drive
/// behavioral induction (Plan D §"Layer 2") without persisting PII or secrets.
/// </summary>
/// <remarks>
/// Producer-side helpers like <see cref="TrajectoryArgumentRedactor.ToShape"/> enforce
/// the redaction; consumers MUST NOT store raw values via this record. The deny-list of
/// secret-shaped argument names is part of the redactor so the contract is consistent
/// regardless of which interceptor produces the event.
/// </remarks>
public sealed record TrajectoryEvent
{
    /// <summary>Stable id for this event (UUIDv7 / opaque). Used as primary key in stores.</summary>
    public required string EventId { get; init; }

    /// <summary>Wall-clock time the event was emitted.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Event name (e.g. <c>tool.call</c>, <c>design.list</c>, <c>design.validate</c>). Inducer keys behavioral patterns on this.</summary>
    public required string EventName { get; init; }

    /// <summary>The bound operation kind from the substrate's <see cref="OntologyOperation"/>.</summary>
    public required OntologyOperation Operation { get; init; }

    /// <summary>Agent id that originated the call (from <c>AgentContext.AgentName</c>). Null for unidentified contexts.</summary>
    public string? AgentId { get; init; }

    /// <summary>Run id (from <c>AgentContext.RunId</c>). Null when the agent isn't journaling.</summary>
    public string? RunId { get; init; }

    /// <summary>The bound concept — for south this is the projected tool name; for north it's the verb (e.g. <c>vais.validate</c>).</summary>
    public string? ConceptName { get; init; }

    /// <summary>
    /// <c>"north"</c> | <c>"south"</c>. Lets the inducer partition the corpus by transport
    /// (authoring vs use traces) — research §9.
    /// </summary>
    public string? Transport { get; init; }

    /// <summary>
    /// Redacted shape of the arguments: arg-name → JSON kind (<c>string</c>, <c>number</c>,
    /// <c>object</c>, ...). Secret-shaped names (apiKey, token, password, ...) are
    /// omitted entirely; raw values are never stored.
    /// </summary>
    public IReadOnlyDictionary<string, string>? ArgumentsShape { get; init; }

    /// <summary>Outcome category + optional error type. Null when the producer hasn't observed an outcome yet (request-phase only).</summary>
    public TrajectoryOutcome? Outcome { get; init; }

    /// <summary>Version of the bound ontology when the event was produced. Carries the curated-↔-induced gap analysis (research §"The closed loop").</summary>
    public string? OntologyVersion { get; init; }

    /// <summary>Wall-clock duration from request-phase to response-phase. Null when the operation didn't complete.</summary>
    public TimeSpan? Duration { get; init; }
}

/// <summary>
/// Pure helper that turns raw argument JSON into the redacted shape stored in
/// <see cref="TrajectoryEvent.ArgumentsShape"/>. Centralized so every producer follows the
/// same secret-name deny-list and value-omission contract.
/// </summary>
/// <remarks>
/// Redaction policy:
/// <list type="number">
///   <item><description>Arg names matching the secret deny-list (case-insensitive substring on <c>apiKey</c>, <c>token</c>, <c>password</c>, <c>secret</c>, <c>auth</c>, <c>credential</c>, <c>privateKey</c>) are OMITTED — neither name nor value appear in the shape.</description></item>
///   <item><description>For surviving arg names, the value's <see cref="JsonValueKind"/> is recorded (e.g. <c>string</c>, <c>number</c>). Raw values are NEVER stored.</description></item>
/// </list>
/// Deployers can extend the deny-list per deployment with <see cref="WithAdditionalSecretNameSubstrings"/>.
/// </remarks>
public sealed class TrajectoryArgumentRedactor
{
    private static readonly string[] DefaultSecretNameSubstrings =
    [
        "apikey", "api_key", "token", "password", "secret", "auth", "credential", "privatekey", "private_key", "passphrase",
    ];

    /// <summary>Singleton with the default deny-list. Use this unless a deployment needs custom secret-name patterns.</summary>
    public static TrajectoryArgumentRedactor Default { get; } = new(DefaultSecretNameSubstrings);

    private readonly string[] _secretSubstrings;

    private TrajectoryArgumentRedactor(string[] secretSubstrings)
    {
        _secretSubstrings = secretSubstrings;
    }

    /// <summary>Build a redactor extending the default deny-list with additional case-insensitive substrings.</summary>
    public static TrajectoryArgumentRedactor WithAdditionalSecretNameSubstrings(params string[] additional)
    {
        ArgumentNullException.ThrowIfNull(additional);
        var merged = new string[DefaultSecretNameSubstrings.Length + additional.Length];
        DefaultSecretNameSubstrings.CopyTo(merged, 0);
        for (var i = 0; i < additional.Length; i++) merged[DefaultSecretNameSubstrings.Length + i] = additional[i].ToLowerInvariant();
        return new TrajectoryArgumentRedactor(merged);
    }

    /// <summary>Produce the redacted shape map for <paramref name="arguments"/>. Non-object inputs yield an empty map.</summary>
    public IReadOnlyDictionary<string, string> ToShape(JsonElement arguments)
    {
        var shape = new Dictionary<string, string>(StringComparer.Ordinal);
        if (arguments.ValueKind != JsonValueKind.Object) return shape;
        foreach (var prop in arguments.EnumerateObject())
        {
            if (IsSecretShapedName(prop.Name)) continue;
            shape[prop.Name] = prop.Value.ValueKind.ToString().ToLowerInvariant();
        }
        return shape;
    }

    /// <summary>Returns true iff <paramref name="argumentName"/> matches the deny-list (case-insensitive substring).</summary>
    public bool IsSecretShapedName(string argumentName)
    {
        var lower = argumentName.ToLowerInvariant();
        for (var i = 0; i < _secretSubstrings.Length; i++)
            if (lower.Contains(_secretSubstrings[i], StringComparison.Ordinal)) return true;
        return false;
    }
}
