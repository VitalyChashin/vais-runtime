// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;

namespace Vais.Agents.Cli.Commands;

/// <summary>
/// Runs a <c>docker</c> sub-command with inherited stdio so the user sees
/// Docker's own progress output. Used by <see cref="PluginBuildCommand"/>
/// and <see cref="PluginPushCommand"/> (image mode).
/// </summary>
internal static class DockerRunner
{
    /// <summary>
    /// Starts <c>docker <paramref name="dockerArgs"/></c> with inherited stdio
    /// and returns its exit code when it finishes.
    /// </summary>
    internal static async Task<int> RunAsync(string dockerArgs, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("docker", dockerArgs)
        {
            UseShellExecute = false,
        };
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start docker process.");
        await process.WaitForExitAsync(ct);
        return process.ExitCode;
    }

    /// <summary>
    /// Checks whether a local Docker image exists by running <c>docker image inspect</c>
    /// with suppressed output. Returns <c>true</c> when the image is present (exit 0).
    /// </summary>
    internal static async Task<bool> ImageExistsAsync(string image, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("docker", $"image inspect {image}")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start docker process.");
        await process.WaitForExitAsync(ct);
        return process.ExitCode == 0;
    }
}
