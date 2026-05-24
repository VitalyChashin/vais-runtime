// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Vais.Agents.Control.InProcess;

/// <summary>
/// In-process <see cref="IMcpServerLifecycleManager"/> backed by any
/// <see cref="IMcpServerRegistry"/>. Routes all verbs through policy + audit middleware.
/// </summary>
public sealed class McpServerLifecycleManager : IMcpServerLifecycleManager
{
    private readonly IMcpServerRegistry _registry;
    private readonly IAgentPolicyEngine _policy;
    private readonly IAuditLog _audit;
    private readonly IAgentContextAccessor _contextAccessor;
    private readonly ILogger<McpServerLifecycleManager> _logger;

    /// <summary>Construct a manager. Registry is required; all other dependencies are optional.</summary>
    public McpServerLifecycleManager(
        IMcpServerRegistry registry,
        IAgentPolicyEngine? policy = null,
        IAuditLog? audit = null,
        IAgentContextAccessor? contextAccessor = null,
        ILogger<McpServerLifecycleManager>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
        _policy = policy ?? NullAgentPolicyEngine.Instance;
        _audit = audit ?? NullAuditLog.Instance;
        _contextAccessor = contextAccessor ?? new AsyncLocalAgentContextAccessorFallback();
        _logger = logger ?? NullLogger<McpServerLifecycleManager>.Instance;
    }

    /// <inheritdoc />
    public async ValueTask<McpServerHandle> CreateAsync(McpServerManifest manifest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var principal = SynthesizePrincipal();
        await GateAsync(PolicyOperation.McpServerCreate, manifest.Id, manifest.Version, principal, ct).ConfigureAwait(false);

        string? errorType = null;
        try
        {
            var existing = await _registry.GetAsync(manifest.Id, manifest.Version, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                throw new McpServerConflictException(manifest.Id, manifest.Version);
            }
            await _registry.RegisterAsync(manifest, ct).ConfigureAwait(false);
            return new McpServerHandle(manifest.Id, manifest.Version);
        }
        catch (Exception ex)
        {
            errorType = ex.GetType().Name;
            throw;
        }
        finally
        {
            await AuditAsync(PolicyOperation.McpServerCreate, manifest.Id, manifest.Version, principal, allowed: true, denyReason: null, errorType).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask<McpServerHandle> UpdateAsync(McpServerHandle handle, McpServerManifest newManifest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(newManifest);
        var principal = SynthesizePrincipal();
        await GateAsync(PolicyOperation.McpServerUpdate, handle.Id, handle.Version, principal, ct).ConfigureAwait(false);

        string? errorType = null;
        try
        {
            await _registry.RegisterAsync(newManifest, ct).ConfigureAwait(false);
            if (!string.Equals(handle.Version, newManifest.Version, StringComparison.Ordinal))
            {
                await _registry.RemoveAsync(handle.Id, handle.Version, ct).ConfigureAwait(false);
            }
            return new McpServerHandle(newManifest.Id, newManifest.Version);
        }
        catch (Exception ex)
        {
            errorType = ex.GetType().Name;
            throw;
        }
        finally
        {
            await AuditAsync(PolicyOperation.McpServerUpdate, handle.Id, handle.Version, principal, allowed: true, denyReason: null, errorType).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask<McpServerStatus> QueryAsync(McpServerHandle handle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var principal = SynthesizePrincipal();
        await GateAsync(PolicyOperation.McpServerQuery, handle.Id, handle.Version, principal, ct).ConfigureAwait(false);

        try
        {
            var manifest = await _registry.GetAsync(handle.Id, handle.Version, ct).ConfigureAwait(false);
            if (manifest is null)
            {
                throw new McpServerHandleNotFoundException(handle.Id, handle.Version);
            }
            return new McpServerStatus(handle, manifest.Virtual, DateTimeOffset.UtcNow);
        }
        finally
        {
            await AuditAsync(PolicyOperation.McpServerQuery, handle.Id, handle.Version, principal, allowed: true, denyReason: null, errorType: null).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask EvictAsync(McpServerHandle handle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var principal = SynthesizePrincipal();
        await GateAsync(PolicyOperation.McpServerEvict, handle.Id, handle.Version, principal, ct).ConfigureAwait(false);

        string? errorType = null;
        try
        {
            var manifest = await _registry.GetAsync(handle.Id, handle.Version, ct).ConfigureAwait(false);
            if (manifest is null)
            {
                throw new McpServerHandleNotFoundException(handle.Id, handle.Version);
            }
            await _registry.RemoveAsync(handle.Id, handle.Version, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            errorType = ex.GetType().Name;
            throw;
        }
        finally
        {
            await AuditAsync(PolicyOperation.McpServerEvict, handle.Id, handle.Version, principal, allowed: true, denyReason: null, errorType).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<McpServerManifest> ListAsync(
        string? labelPrefix = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var manifest in _registry.ListAsync(labelPrefix, ct).ConfigureAwait(false))
        {
            yield return manifest;
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async ValueTask GateAsync(PolicyOperation op, string serverId, string version, AgentPrincipal? principal, CancellationToken ct)
    {
        var decision = await _policy.EvaluateAsync(op, manifest: null, principal, ct).ConfigureAwait(false);
        if (decision.IsAllowed) return;
        var reason = decision.Reason ?? "policy denied";
        await AuditAsync(op, serverId, version, principal, allowed: false, denyReason: reason, errorType: null).ConfigureAwait(false);
        throw new AgentPolicyDeniedException(op, reason);
    }

    private async ValueTask AuditAsync(
        PolicyOperation op,
        string? serverId,
        string? version,
        AgentPrincipal? principal,
        bool allowed,
        string? denyReason,
        string? errorType)
    {
        var entry = new AuditLogEntry(
            At: DateTimeOffset.UtcNow,
            Operation: op,
            AgentId: serverId,
            AgentVersion: version,
            PrincipalId: principal?.Id ?? "anonymous",
            TenantId: principal?.TenantId,
            Allowed: allowed,
            DenyReason: denyReason,
            ErrorType: errorType);
        try
        {
            await _audit.AppendAsync(entry).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Audit log {AuditType} threw; swallowed.", _audit.GetType().Name);
        }
    }

    private AgentPrincipal? SynthesizePrincipal()
    {
        var ctx = _contextAccessor.Current;
        if (ctx.UserId is { Length: > 0 } userId)
        {
            return new AgentPrincipal(userId, ctx.TenantId, ctx.Scopes);
        }
        return null;
    }

    private sealed class AsyncLocalAgentContextAccessorFallback : IAgentContextAccessor
    {
        public AgentContext Current => AgentContext.Empty;
    }
}
