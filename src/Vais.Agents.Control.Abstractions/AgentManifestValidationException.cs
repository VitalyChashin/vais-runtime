// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Immutable;

namespace Vais.Agents.Control;

/// <summary>
/// Thrown by <see cref="IAgentManifestLoader"/> implementations when the supplied
/// content parses but violates one or more schema rules (required fields, label-key
/// regex, semver format, duplicate ids, mutually-exclusive field combinations).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Errors"/> carries every rule violation the validator found in a
/// single pass — one exception per load call, not per error. HTTP layer maps to
/// RFC 7807 <c>urn:vais-agents:manifest-invalid</c> Problem Details with the
/// error list on the response body.
/// </para>
/// </remarks>
public sealed class AgentManifestValidationException : Exception
{
    /// <summary>Create a validation exception carrying one or more rule violations.</summary>
    /// <exception cref="ArgumentException"><paramref name="errors"/> is null or empty.</exception>
    public AgentManifestValidationException(IEnumerable<string> errors)
        : base(BuildMessage(errors))
    {
        Errors = errors.ToImmutableArray();
        if (Errors.Length == 0)
        {
            throw new ArgumentException("At least one error must be supplied.", nameof(errors));
        }
    }

    /// <summary>Every rule violation found during validation. Never empty.</summary>
    public ImmutableArray<string> Errors { get; }

    private static string BuildMessage(IEnumerable<string> errors)
    {
        var list = errors.ToList();
        return list.Count switch
        {
            0 => "Manifest validation failed.",
            1 => $"Manifest validation failed: {list[0]}",
            _ => $"Manifest validation failed ({list.Count} errors): {string.Join("; ", list)}",
        };
    }
}
