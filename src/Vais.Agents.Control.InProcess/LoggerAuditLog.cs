// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging;

namespace Vais.Agents.Control.InProcess;

/// <summary>
/// <see cref="IAuditLog"/> that writes every <see cref="AuditLogEntry"/> through
/// an <see cref="ILogger"/> using structured fields. Pairs with any log aggregator
/// (OTel log exporter, Seq, ELK, Loki) without a new adapter; the audit trail
/// shows up alongside the rest of the host's logs under the
/// <c>Vais.Agents.Control.Audit</c> category by default.
/// </summary>
/// <remarks>
/// <para>
/// <b>Severity.</b> Allowed + successful verbs log at <see cref="LogLevel.Information"/>;
/// allowed + failed ones at <see cref="LogLevel.Warning"/>; denied verbs at
/// <see cref="LogLevel.Warning"/> too. Consumers who want errors at a different
/// level can wrap or replace.
/// </para>
/// <para>
/// <b>Swallow semantics.</b> Logger calls are near-free and rarely throw; if they
/// do, <see cref="AppendAsync"/> still must not throw into the caller (the verb
/// already ran), so exceptions are caught + silently dropped. The audit trail is
/// best-effort — correctness responsibilities belong to the underlying logger
/// infrastructure.
/// </para>
/// </remarks>
public sealed class LoggerAuditLog : IAuditLog
{
    private readonly ILogger<LoggerAuditLog> _logger;

    /// <summary>Construct over a category-scoped logger.</summary>
    public LoggerAuditLog(ILogger<LoggerAuditLog> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc />
    public ValueTask AppendAsync(AuditLogEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        try
        {
            var level = entry switch
            {
                { Allowed: false } => LogLevel.Warning,
                { ErrorType: not null } => LogLevel.Warning,
                _ => LogLevel.Information,
            };

            // Structured log — each field appears on the LogRecord for any OTel exporter / Seq / ELK.
            _logger.Log(
                level,
                "AgentControlPlane audit: {Operation} agent={AgentId} version={AgentVersion} principal={PrincipalId} tenant={TenantId} allowed={Allowed} denyReason={DenyReason} errorType={ErrorType}",
                entry.Operation,
                entry.AgentId ?? "(null)",
                entry.AgentVersion ?? "(null)",
                entry.PrincipalId,
                entry.TenantId ?? "(null)",
                entry.Allowed,
                entry.DenyReason ?? "(null)",
                entry.ErrorType ?? "(null)");
        }
        catch
        {
            // Logger-side failures must not break the verb.
        }
        return ValueTask.CompletedTask;
    }
}
