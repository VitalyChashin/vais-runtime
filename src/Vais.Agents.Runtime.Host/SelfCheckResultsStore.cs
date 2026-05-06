// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Host;

/// <summary>
/// Singleton holder populated by <see cref="RuntimeSelfCheckService"/> on startup.
/// <see cref="SelfCheckHealthCheck"/> reads results on every /readyz poll.
/// </summary>
internal sealed class SelfCheckResultsStore
{
    private volatile IReadOnlyList<SelfCheckResult>? _results;

    public bool IsComplete => _results is not null;
    public IReadOnlyList<SelfCheckResult> Results => _results ?? [];

    internal void SetResults(IReadOnlyList<SelfCheckResult> results) => _results = results;
}
