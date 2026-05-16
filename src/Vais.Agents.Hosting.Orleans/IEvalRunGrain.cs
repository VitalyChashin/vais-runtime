// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Hosting.Orleans;

/// <summary>
/// Grain that orchestrates a single eval-suite run: processes cases sequentially,
/// evaluates assertions, and writes results to the eval result store.
/// Primary key = eval run id (UUID string).
/// Suite manifest is passed as JSON so <c>EvalSuiteManifest</c> (in the Orleans-free
/// Abstractions assembly) does not need <c>[GenerateSerializer]</c>.
/// </summary>
public interface IEvalRunGrain : IGrainWithStringKey
{
    /// <summary>Initialize and start the eval run. <paramref name="suiteJson"/> is a serialized <c>EvalSuiteManifest</c>. Kicks off <see cref="ProcessNextCaseAsync"/>.</summary>
    ValueTask StartAsync(string suiteJson, string workspace, CancellationToken ct = default);

    /// <summary>Process the current case (called by self-scheduling self-calls).</summary>
    ValueTask ProcessNextCaseAsync(CancellationToken ct = default);

    /// <summary>Request cancellation. In-flight case may complete; no further cases are dispatched.</summary>
    ValueTask CancelAsync(CancellationToken ct = default);
}
