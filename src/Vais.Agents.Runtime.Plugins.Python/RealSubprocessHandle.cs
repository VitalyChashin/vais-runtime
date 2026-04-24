// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;

namespace Vais.Agents.Runtime.Plugins.Python;

/// <summary>
/// Production <see cref="ISubprocessHandle"/> that wraps a real <see cref="Process"/>
/// launched with redirected stdin/stdout/stderr.
/// </summary>
internal sealed class RealSubprocessHandle : ISubprocessHandle
{
    private static readonly TimeSpan GracefulExitTimeout = TimeSpan.FromSeconds(5);

    private readonly Process _process;

    internal RealSubprocessHandle(PythonPluginDescriptor descriptor)
    {
        var psi = new ProcessStartInfo
        {
            FileName = descriptor.InterpreterPath,
            Arguments = descriptor.EntrypointPath,
            WorkingDirectory = descriptor.PluginDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var (key, value) in descriptor.SecretRefs)
            psi.Environment[key] = value;

        _process = new Process { StartInfo = psi };
        _process.Start();

        Exited = _process.WaitForExitAsync();
    }

    public int ProcessId => _process.Id;
    public Stream StandardInput => _process.StandardInput.BaseStream;
    public Stream StandardOutput => _process.StandardOutput.BaseStream;
    public TextReader StandardError => _process.StandardError;
    public Task Exited { get; }

    public void Kill()
    {
        try { _process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { } // already exited
        catch (Exception) { }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var gracefulCts = new CancellationTokenSource(GracefulExitTimeout);
            await _process.WaitForExitAsync(gracefulCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill();
            try { await _process.WaitForExitAsync().ConfigureAwait(false); }
            catch { }
        }
        finally
        {
            _process.Dispose();
        }
    }
}
