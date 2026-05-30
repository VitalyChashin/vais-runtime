// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Severity of a mechanical failure signal, mirroring the Langfuse observation levels
/// (DEFAULT / WARNING / ERROR). The distinguishing rule is <b>recovery</b>: a failure the
/// runtime recovered from (a tool error fed back to the model, an LLM call that succeeded
/// after a retry or a provider fallback) is <see cref="Warning"/> — it must stay visible,
/// but it did not abort the operation. A failure that aborted the turn or run is
/// <see cref="Error"/>. A clean signal is <see cref="Default"/>.
/// </summary>
/// <remarks>
/// This is the per-signal input to the run-level health rollup: a run's health is the
/// worst level among its descendants, so a single <see cref="Warning"/> leaf marks an
/// otherwise-green run as <em>degraded</em> rather than letting the recovery hide it.
/// </remarks>
public enum FailureLevel
{
    /// <summary>No failure — a clean, successful signal.</summary>
    Default = 0,

    /// <summary>A recovered mechanical failure (retried, fell back, or fed back to the model). Degraded, not fatal.</summary>
    Warning = 1,

    /// <summary>An unrecovered failure that aborted the turn or run.</summary>
    Error = 2,
}
