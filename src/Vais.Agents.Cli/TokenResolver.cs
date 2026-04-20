// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Cli;

/// <summary>
/// Applies the bearer-token precedence chain. kubectl-style: explicit
/// flag beats env var; env var beats active context's user record; no
/// source found → unauthenticated (caller passes null).
/// </summary>
internal static class TokenResolver
{
    /// <summary>Environment variable consulted after <c>--token</c>.</summary>
    public const string TokenEnvVar = "VAIS_TOKEN";

    /// <summary>
    /// Resolve the effective bearer token. Returns null when no source
    /// carries one; the CLI then attaches no Authorization header.
    /// </summary>
    /// <param name="tokenFlag">The <c>--token</c> flag value. Wins when non-empty.</param>
    /// <param name="contextUser">Active context's user record (may be null if no context selected or user missing).</param>
    public static string? Resolve(string? tokenFlag, VaisUser? contextUser)
    {
        if (!string.IsNullOrWhiteSpace(tokenFlag))
        {
            return tokenFlag;
        }
        var env = Environment.GetEnvironmentVariable(TokenEnvVar);
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }
        if (contextUser is null)
        {
            return null;
        }
        if (!string.IsNullOrWhiteSpace(contextUser.Token))
        {
            return contextUser.Token;
        }
        if (!string.IsNullOrWhiteSpace(contextUser.TokenFile) && File.Exists(contextUser.TokenFile))
        {
            return File.ReadAllText(contextUser.TokenFile).Trim();
        }
        return null;
    }
}
