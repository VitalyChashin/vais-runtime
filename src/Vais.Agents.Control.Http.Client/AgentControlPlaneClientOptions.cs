// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.Http;

/// <summary>
/// Client-side knobs for <see cref="AgentControlPlaneClient"/>. Wire into DI via
/// <c>services.Configure&lt;AgentControlPlaneClientOptions&gt;(...)</c> or pass a
/// pre-built instance to the two-arg <see cref="AgentControlPlaneClient(System.Net.Http.HttpClient, AgentControlPlaneClientOptions)"/>
/// ctor.
/// </summary>
public sealed class AgentControlPlaneClientOptions
{
    /// <summary>
    /// When <c>true</c>, the client generates a fresh <c>Idempotency-Key</c>
    /// header for every write call that doesn't already carry an explicit
    /// <c>idempotencyKey</c> parameter. Useful for fire-and-forget scenarios
    /// where the caller doesn't manage retries themselves. Default: <c>false</c>
    /// (preserves pre-v0.11 behaviour — no header unless the caller supplies
    /// one).
    /// </summary>
    public bool AutoGenerateIdempotencyKey { get; set; }

    /// <summary>
    /// Factory producing an idempotency key when
    /// <see cref="AutoGenerateIdempotencyKey"/> is <c>true</c> and the caller
    /// didn't supply one. Default: <c>Guid.NewGuid().ToString("N")</c> — a
    /// collision-free 32-hex identifier matching the <c>RunId</c> factory
    /// shape used elsewhere in the library.
    /// </summary>
    public Func<string>? IdempotencyKeyFactory { get; set; }
}
