// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Host;

/// <summary>
/// Startup hosted service that probes every registered <see cref="ISelfCheckProbe"/> in
/// parallel (3 s per probe, 15 s overall cap) and stores results in
/// <see cref="SelfCheckResultsStore"/> for the <see cref="SelfCheckHealthCheck"/>.
/// Registered last so all event-store schema initializers run first.
/// </summary>
internal sealed class RuntimeSelfCheckService : IHostedService
{
    private readonly IReadOnlyList<ISelfCheckProbe> _probes;
    private readonly SelfCheckResultsStore _store;
    private readonly ILogger<RuntimeSelfCheckService> _logger;

    public RuntimeSelfCheckService(
        IEnumerable<ISelfCheckProbe> probes,
        SelfCheckResultsStore store,
        ILogger<RuntimeSelfCheckService> logger)
    {
        _probes = [.. probes];
        _store = store;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_probes.Count == 0)
        {
            _store.SetResults([]);
            return;
        }

        using var totalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        totalCts.CancelAfter(TimeSpan.FromSeconds(15));

        var results = await Task.WhenAll(_probes.Select(p => RunProbeAsync(p, totalCts.Token)));

        foreach (var r in results)
        {
            if (r.IsOk)
                _logger.LogInformation("Self-check {Service}: OK", r.ServiceName);
            else
                _logger.LogWarning("Self-check {Service}: FAIL — {Reason}", r.ServiceName, r.FailureReason);
        }

        _store.SetResults(results);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task<SelfCheckResult> RunProbeAsync(ISelfCheckProbe probe, CancellationToken totalCt)
    {
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(totalCt);
        probeCts.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            return await probe.ProbeAsync(probeCts.Token);
        }
        catch (Exception ex)
        {
            return new SelfCheckResult(probe.ServiceName, probe.IsRequired, false, ex.Message);
        }
    }
}
