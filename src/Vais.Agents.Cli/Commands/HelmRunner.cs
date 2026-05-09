// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;

namespace Vais.Agents.Cli.Commands;

/// <summary>
/// Runs <c>helm</c> sub-commands with inherited stdio.
/// Used by <see cref="PluginDeployCommand"/> for Kubernetes deployments.
/// </summary>
internal static class HelmRunner
{
    /// <summary>
    /// Starts <c>helm <paramref name="helmArgs"/></c> with inherited stdio
    /// and returns its exit code when it finishes.
    /// </summary>
    internal static async Task<int> RunAsync(string helmArgs, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("helm", helmArgs)
        {
            UseShellExecute = false,
        };
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start helm process.");
        await process.WaitForExitAsync(ct);
        return process.ExitCode;
    }
}
