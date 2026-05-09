// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;

namespace Vais.Agents.Cli.Commands;

/// <summary>
/// Runs <c>kubectl</c> sub-commands for kubernetes topology operations.
/// </summary>
internal static class KubectlRunner
{
    /// <summary>
    /// Starts <c>kubectl <paramref name="kubectlArgs"/></c> with inherited stdio
    /// and returns its exit code when it finishes.
    /// </summary>
    internal static async Task<int> RunAsync(string kubectlArgs, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("kubectl", kubectlArgs)
        {
            UseShellExecute = false,
        };
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start kubectl process.");
        await process.WaitForExitAsync(ct);
        return process.ExitCode;
    }

    /// <summary>
    /// Runs <c>kubectl <paramref name="kubectlArgs"/></c> and returns trimmed stdout,
    /// or <see langword="null"/> if the process fails or <c>kubectl</c> is not available.
    /// </summary>
    internal static async Task<string?> GetOutputAsync(string kubectlArgs, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("kubectl", kubectlArgs)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(psi);
            if (process is null) return null;
            var output = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch
        {
            return null;
        }
    }
}
