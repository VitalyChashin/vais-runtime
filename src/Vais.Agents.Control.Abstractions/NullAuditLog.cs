// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control;

/// <summary>
/// No-op <see cref="IAuditLog"/>. The default when no explicit audit sink is
/// wired — preserves pre-v0.6 behaviour (no audit trail). Real implementations
/// (structured-logger-backed, DB-backed, event-bus-backed) slot in via DI.
/// </summary>
public sealed class NullAuditLog : IAuditLog
{
    /// <summary>Shared singleton instance. Stateless.</summary>
    public static readonly NullAuditLog Instance = new();

    private NullAuditLog() { }

    /// <inheritdoc />
    public ValueTask AppendAsync(AuditLogEntry entry, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}
