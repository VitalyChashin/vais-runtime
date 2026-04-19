// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Builds and parses the string key for <see cref="IAgentSessionGrain"/> —
/// <c>"{agentId}/{sessionId}"</c>. Centralised so silo-side and client-side agree
/// on the encoding.
/// </summary>
public static class OrleansSessionGrainKey
{
    internal const char Separator = '/';

    /// <summary>
    /// Produce the grain key for <paramref name="agentId"/> / <paramref name="sessionId"/>.
    /// </summary>
    /// <exception cref="ArgumentException">Either id is null, empty, whitespace, or contains <c>/</c>.</exception>
    public static string Build(string agentId, string sessionId)
    {
        Validate(agentId, nameof(agentId));
        Validate(sessionId, nameof(sessionId));
        return $"{agentId}{Separator}{sessionId}";
    }

    /// <summary>
    /// Parse a grain key back into its (agentId, sessionId) tuple.
    /// </summary>
    /// <exception cref="ArgumentException">Key is not of the required shape.</exception>
    public static (string AgentId, string SessionId) Parse(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var slash = key.IndexOf(Separator);
        if (slash <= 0 || slash >= key.Length - 1)
        {
            throw new ArgumentException(
                $"Session grain key must be of the form 'agentId{Separator}sessionId'; got '{key}'.",
                nameof(key));
        }
        return (key[..slash], key[(slash + 1)..]);
    }

    private static void Validate(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        if (value.IndexOf(Separator) >= 0)
        {
            throw new ArgumentException(
                $"Session identifiers must not contain '{Separator}'; got '{value}'.",
                paramName);
        }
    }
}
