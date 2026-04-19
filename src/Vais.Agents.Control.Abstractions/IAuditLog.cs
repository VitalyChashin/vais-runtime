// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control;

/// <summary>
/// Append-only sink for <see cref="AuditLogEntry"/> records. Written once per
/// lifecycle verb after the verb resolves — whether it passed policy, failed
/// policy, succeeded, or threw. Consumers slot in implementations that persist
/// to whatever durable medium they need (log aggregator, database, event bus).
/// </summary>
/// <remarks>
/// <para>
/// <b>Failure semantics.</b> Audit-write failures must not break the lifecycle
/// verb — the caller's operation already happened (or was denied) by the time
/// the audit entry is written. Implementations should swallow exceptions and
/// surface them via an out-of-band telemetry channel. The shipped
/// <see cref="NullAuditLog"/> default drops writes on the floor.
/// </para>
/// </remarks>
public interface IAuditLog
{
    /// <summary>Append a single audit entry. Must not throw into the caller.</summary>
    ValueTask AppendAsync(AuditLogEntry entry, CancellationToken cancellationToken = default);
}
