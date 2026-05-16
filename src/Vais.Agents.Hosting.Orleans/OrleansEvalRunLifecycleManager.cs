// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using Vais.Agents.Eval;

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// <see cref="IEvalRunLifecycleManager"/> backed by <see cref="IEvalRunGrain"/> grains.
/// Each eval run is a grain keyed by the eval run id (UUID string).
/// </summary>
public sealed class OrleansEvalRunLifecycleManager : IEvalRunLifecycleManager
{
    private readonly IGrainFactory _grainFactory;
    private readonly IEvalSuiteRegistry _suiteRegistry;
    private readonly IEvalResultStore _resultStore;

    /// <summary>DI ctor.</summary>
    public OrleansEvalRunLifecycleManager(
        IGrainFactory grainFactory,
        IEvalSuiteRegistry suiteRegistry,
        IEvalResultStore resultStore)
    {
        ArgumentNullException.ThrowIfNull(grainFactory);
        ArgumentNullException.ThrowIfNull(suiteRegistry);
        ArgumentNullException.ThrowIfNull(resultStore);
        _grainFactory = grainFactory;
        _suiteRegistry = suiteRegistry;
        _resultStore = resultStore;
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    /// <inheritdoc/>
    public async ValueTask<string> StartRunAsync(string suiteName, string workspace, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suiteName);

        var suite = await _suiteRegistry.GetAsync(suiteName, version: null, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Eval suite '{suiteName}' not found.");

        var suiteJson = JsonSerializer.Serialize(suite, JsonOpts);
        var evalRunId = Guid.NewGuid().ToString("N");
        var grain = _grainFactory.GetGrain<IEvalRunGrain>(evalRunId);
        await grain.StartAsync(suiteJson, workspace, ct).ConfigureAwait(false);
        return evalRunId;
    }

    /// <inheritdoc/>
    public async ValueTask CancelRunAsync(string evalRunId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evalRunId);
        var grain = _grainFactory.GetGrain<IEvalRunGrain>(evalRunId);
        await grain.CancelAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<EvalRunDetail?> GetRunDetailAsync(string evalRunId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evalRunId);
        return await _resultStore.GetRunAsync(evalRunId, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<EvalRunSummary>> ListRunsAsync(string? suiteName = null, int limit = 50, CancellationToken ct = default)
        => await _resultStore.ListRunsAsync(suiteName, limit, ct).ConfigureAwait(false);
}
