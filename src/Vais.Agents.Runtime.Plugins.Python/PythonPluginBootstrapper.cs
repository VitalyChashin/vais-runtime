// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Vais.Agents.Runtime.Plugins.Python;

/// <summary>
/// Provisions a Python virtual environment inside a plugin directory on first push.
/// Runs <c>python3.11 -m venv .venv</c> then <c>.venv/bin/pip install -q -e .</c>.
/// Skipped when <c>.venv/bin/python</c> already exists.
/// </summary>
internal sealed class PythonPluginBootstrapper
{
    private readonly ILogger<PythonPluginBootstrapper> _logger;
    private readonly Func<ProcessStartInfo, CancellationToken, Task<(int ExitCode, string Output)>> _runner;

    internal PythonPluginBootstrapper(
        ILoggerFactory? loggerFactory = null,
        Func<ProcessStartInfo, CancellationToken, Task<(int ExitCode, string Output)>>? runner = null)
    {
        _logger = (loggerFactory ?? NullLoggerFactory.Instance)
            .CreateLogger<PythonPluginBootstrapper>();
        _runner = runner ?? RunProcessAsync;
    }

    /// <summary>
    /// Provisions a venv in <paramref name="pluginDirectory"/>. No-op when the venv already exists.
    /// </summary>
    internal async Task<BootstrapResult> BootstrapAsync(
        string pluginDirectory,
        int timeoutSeconds,
        CancellationToken ct = default)
    {
        var venvPython = Path.Combine(pluginDirectory, ".venv", "bin", "python");
        if (File.Exists(venvPython))
        {
            _logger.LogDebug(
                "python-plugin-bootstrap: .venv already exists in '{Dir}' — skipping.", pluginDirectory);
            return new BootstrapResult(true, null);
        }

        _logger.LogInformation(
            "python-plugin-bootstrap: provisioning venv in '{Dir}' (timeout {Timeout}s).",
            pluginDirectory, timeoutSeconds);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var createVenvPsi = new ProcessStartInfo("python3.11")
        {
            WorkingDirectory = pluginDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        createVenvPsi.ArgumentList.Add("-m");
        createVenvPsi.ArgumentList.Add("venv");
        createVenvPsi.ArgumentList.Add(".venv");
        // --system-site-packages lets the plugin venv inherit pre-installed
        // framework packages (vais-agent-sdk) without a PyPI lookup.
        createVenvPsi.ArgumentList.Add("--system-site-packages");

        var (createExitCode, createOutput) = await _runner(createVenvPsi, cts.Token)
            .ConfigureAwait(false);

        if (createExitCode != 0)
        {
            _logger.LogWarning(
                "[{Urn}] python-plugin-bootstrap: 'python3.11 -m venv .venv' failed (exit {Code}) " +
                "in '{Dir}': {Output}",
                PythonPluginUrns.BootstrapFailed, createExitCode, pluginDirectory, createOutput);
            return new BootstrapResult(false, createOutput);
        }

        var pipPsi = new ProcessStartInfo(Path.Combine(pluginDirectory, ".venv", "bin", "pip"))
        {
            WorkingDirectory = pluginDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        pipPsi.ArgumentList.Add("install");
        pipPsi.ArgumentList.Add("-q");
        pipPsi.ArgumentList.Add("-e");
        pipPsi.ArgumentList.Add(".");

        var (installExitCode, installOutput) = await _runner(pipPsi, cts.Token)
            .ConfigureAwait(false);

        if (installExitCode != 0)
        {
            _logger.LogWarning(
                "[{Urn}] python-plugin-bootstrap: pip install failed (exit {Code}) in '{Dir}': {Output}",
                PythonPluginUrns.BootstrapFailed, installExitCode, pluginDirectory, installOutput);
            return new BootstrapResult(false, installOutput);
        }

        _logger.LogInformation(
            "python-plugin-bootstrap: venv ready in '{Dir}'.", pluginDirectory);
        return new BootstrapResult(true, null);
    }

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(
        ProcessStartInfo psi,
        CancellationToken ct)
    {
        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        var combined = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
        return (process.ExitCode, combined.Trim());
    }
}

/// <param name="Success">Whether the venv was provisioned (or already existed).</param>
/// <param name="ErrorOutput">Process stderr on failure; <see langword="null"/> on success.</param>
internal readonly record struct BootstrapResult(bool Success, string? ErrorOutput);
