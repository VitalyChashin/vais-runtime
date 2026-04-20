// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Spectre.Console;
using Vais.Agents.Control.Http;

namespace Vais.Agents.Cli;

/// <summary>
/// Maps <see cref="AgentControlPlaneException"/> instances to POSIX
/// exit codes + a human-readable stderr message. Keeps exit-code
/// semantics consistent across every verb that hits the control plane.
/// </summary>
internal static class ProblemDetailsParser
{
    /// <summary>Success — kept for symmetry.</summary>
    public const int ExitSuccess = 0;

    /// <summary>Usage / client-side error (bad flags, missing file, etc.).</summary>
    public const int ExitUsageError = 1;

    /// <summary>API error — non-2xx from the control plane (not 401/403 specifically).</summary>
    public const int ExitApiError = 2;

    /// <summary>Policy denial — 403 with <c>urn:vais-agents:policy-denied</c> URN.</summary>
    public const int ExitPolicyDenied = 3;

    /// <summary>Auth failure — 401 from the control plane.</summary>
    public const int ExitAuthFailure = 4;

    /// <summary>Streamed command interrupted via Ctrl-C / SIGINT.</summary>
    public const int ExitSigInt = 130;

    /// <summary>Known URN for policy-denied responses from the shipped <c>IAgentPolicyEngine</c>.</summary>
    public const string PolicyDeniedUrn = "urn:vais-agents:policy-denied";

    /// <summary>
    /// Print <paramref name="ex"/> to stderr in a readable form and
    /// return the appropriate exit code. Consumers pipe stderr into
    /// grep / jq; CLI stdout stays clean for the happy path.
    /// </summary>
    public static int HandleAndExitCode(AgentControlPlaneException ex, IAnsiConsole stderr)
    {
        ArgumentNullException.ThrowIfNull(ex);
        ArgumentNullException.ThrowIfNull(stderr);

        var code = ex.StatusCode switch
        {
            401 => ExitAuthFailure,
            403 when string.Equals(ex.Type, PolicyDeniedUrn, StringComparison.Ordinal) => ExitPolicyDenied,
            _ => ExitApiError,
        };

        stderr.MarkupLine($"[red]error[/] [grey]{ex.StatusCode}[/] {EscapeMarkup(ex.Title ?? string.Empty)}");
        if (!string.IsNullOrWhiteSpace(ex.Type))
        {
            stderr.MarkupLine($"  [grey]type:[/] {EscapeMarkup(ex.Type)}");
        }
        if (!string.IsNullOrWhiteSpace(ex.Message))
        {
            stderr.MarkupLine($"  [grey]detail:[/] {EscapeMarkup(ex.Message)}");
        }
        return code;
    }

    /// <summary>
    /// Returns true when <paramref name="ex"/> is a 409 Conflict —
    /// the signal <c>vais apply</c> uses to fall back from
    /// <c>CreateAsync</c> to <c>UpdateAsync</c>.
    /// </summary>
    public static bool IsConflict(AgentControlPlaneException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex.StatusCode == 409;
    }

    private static string EscapeMarkup(string value) => value.Replace("[", "[[", StringComparison.Ordinal).Replace("]", "]]", StringComparison.Ordinal);
}
