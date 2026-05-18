// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vais.Agents.Eval.Continuous;

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Hosted service that populates <see cref="IContinuousSuiteIndex"/> from the durable
/// <see cref="IEvalSuiteRegistry"/> at silo startup and re-refreshes every 30 seconds
/// (P11: hot-apply of new sampling suites takes effect within ~30 s without restart).
/// </summary>
internal sealed class ContinuousSuiteActivator : IHostedService, IAsyncDisposable
{
    private readonly IEvalSuiteRegistry _registry;
    private readonly IContinuousSuiteIndex _index;
    private readonly ILogger<ContinuousSuiteActivator> _logger;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public ContinuousSuiteActivator(
        IEvalSuiteRegistry registry,
        IContinuousSuiteIndex index,
        ILogger<ContinuousSuiteActivator> logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(logger);
        _registry = registry;
        _index = index;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await RefreshAsync(cancellationToken);
        _cts = new CancellationTokenSource();
        _loop = RunLoopAsync(_cts.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            try { if (_loop is not null) await _loop.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { }
            _cts.Dispose();
            _cts = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            _cts.Dispose();
            _cts = null;
        }
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch { /* swallow — already cancelled */ }
            _loop = null;
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                await RefreshAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ContinuousSuiteActivator refresh failed");
            }
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        var suites = new List<EvalSuiteManifest>();
        await foreach (var suite in _registry.ListAsync(ct: ct))
            suites.Add(suite);
        _index.Refresh(suites);
        _logger.LogDebug("ContinuousSuiteActivator refreshed index with {Count} active suite(s)", suites.Count);
    }
}
