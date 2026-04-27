// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Vais.Agents.Control.InProcess;

/// <summary>
/// In-process <see cref="IMcpGatewayConfigLifecycleManager"/> backed by any
/// <see cref="IMcpGatewayConfigRegistry"/>. Routes all verbs through policy + audit middleware.
/// </summary>
public sealed class McpGatewayConfigLifecycleManager : IMcpGatewayConfigLifecycleManager
{
    private readonly IMcpGatewayConfigRegistry _registry;
    private readonly IAgentPolicyEngine _policy;
    private readonly IAuditLog _audit;
    private readonly IAgentContextAccessor _contextAccessor;
    private readonly ILogger<McpGatewayConfigLifecycleManager> _logger;

    /// <summary>Construct a manager. Registry is required; all other dependencies are optional.</summary>
    public McpGatewayConfigLifecycleManager(
        IMcpGatewayConfigRegistry registry,
        IAgentPolicyEngine? policy = null,
        IAuditLog? audit = null,
        IAgentContextAccessor? contextAccessor = null,
        ILogger<McpGatewayConfigLifecycleManager>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
        _policy = policy ?? NullAgentPolicyEngine.Instance;
        _audit = audit ?? NullAuditLog.Instance;
        _contextAccessor = contextAccessor ?? new AsyncLocalAgentContextAccessorFallback();
        _logger = logger ?? NullLogger<McpGatewayConfigLifecycleManager>.Instance;
    }

    /// <inheritdoc />
    public async ValueTask<McpGatewayConfigHandle> CreateAsync(McpGatewayConfigManifest manifest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var principal = SynthesizePrincipal();
        await GateAsync(PolicyOperation.McpGatewayConfigCreate, manifest.Id, manifest.Version, principal, ct).ConfigureAwait(false);

        string? errorType = null;
        try
        {
            var existing = await _registry.GetAsync(manifest.Id, manifest.Version, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                throw new McpGatewayConfigConflictException(manifest.Id, manifest.Version);
            }
            await _registry.RegisterAsync(manifest, ct).ConfigureAwait(false);
            return new McpGatewayConfigHandle(manifest.Id, manifest.Version);
        }
        catch (Exception ex)
        {
            errorType = ex.GetType().Name;
            throw;
        }
        finally
        {
            await AuditAsync(PolicyOperation.McpGatewayConfigCreate, manifest.Id, manifest.Version, principal, allowed: true, denyReason: null, errorType).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask<McpGatewayConfigHandle> UpdateAsync(McpGatewayConfigHandle handle, McpGatewayConfigManifest newManifest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(newManifest);
        var principal = SynthesizePrincipal();
        await GateAsync(PolicyOperation.McpGatewayConfigUpdate, handle.Id, handle.Version, principal, ct).ConfigureAwait(false);

        string? errorType = null;
        try
        {
            await _registry.RegisterAsync(newManifest, ct).ConfigureAwait(false);
            if (!string.Equals(handle.Version, newManifest.Version, StringComparison.Ordinal))
            {
                await _registry.RemoveAsync(handle.Id, handle.Version, ct).ConfigureAwait(false);
            }
            return new McpGatewayConfigHandle(newManifest.Id, newManifest.Version);
        }
        catch (Exception ex)
        {
            errorType = ex.GetType().Name;
            throw;
        }
        finally
        {
            await AuditAsync(PolicyOperation.McpGatewayConfigUpdate, handle.Id, handle.Version, principal, allowed: true, denyReason: null, errorType).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask<McpGatewayConfigStatus> QueryAsync(McpGatewayConfigHandle handle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var principal = SynthesizePrincipal();
        await GateAsync(PolicyOperation.McpGatewayConfigQuery, handle.Id, handle.Version, principal, ct).ConfigureAwait(false);

        try
        {
            var manifest = await _registry.GetAsync(handle.Id, handle.Version, ct).ConfigureAwait(false);
            if (manifest is null)
            {
                throw new McpGatewayConfigHandleNotFoundException(handle.Id, handle.Version);
            }
            return new McpGatewayConfigStatus(handle, DateTimeOffset.UtcNow);
        }
        finally
        {
            await AuditAsync(PolicyOperation.McpGatewayConfigQuery, handle.Id, handle.Version, principal, allowed: true, denyReason: null, errorType: null).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask EvictAsync(McpGatewayConfigHandle handle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var principal = SynthesizePrincipal();
        await GateAsync(PolicyOperation.McpGatewayConfigEvict, handle.Id, handle.Version, principal, ct).ConfigureAwait(false);

        string? errorType = null;
        try
        {
            var manifest = await _registry.GetAsync(handle.Id, handle.Version, ct).ConfigureAwait(false);
            if (manifest is null)
            {
                throw new McpGatewayConfigHandleNotFoundException(handle.Id, handle.Version);
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
            await AuditAsync(PolicyOperation.McpGatewayConfigEvict, handle.Id, handle.Version, principal, allowed: true, denyReason: null, errorType).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<McpGatewayConfigManifest> ListAsync(
        string? labelPrefix = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var manifest in _registry.ListAsync(labelPrefix, ct).ConfigureAwait(false))
        {
            yield return manifest;
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async ValueTask GateAsync(PolicyOperation op, string configId, string version, AgentPrincipal? principal, CancellationToken ct)
    {
        var decision = await _policy.EvaluateAsync(op, manifest: null, principal, ct).ConfigureAwait(false);
        if (decision.IsAllowed) return;
        var reason = decision.Reason ?? "policy denied";
        await AuditAsync(op, configId, version, principal, allowed: false, denyReason: reason, errorType: null).ConfigureAwait(false);
        throw new AgentPolicyDeniedException(op, reason);
    }

    private async ValueTask AuditAsync(
        PolicyOperation op,
        string? configId,
        string? version,
        AgentPrincipal? principal,
        bool allowed,
        string? denyReason,
        string? errorType)
    {
        var entry = new AuditLogEntry(
            At: DateTimeOffset.UtcNow,
            Operation: op,
            AgentId: configId,
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
            return new AgentPrincipal(userId, ctx.TenantId, Scopes: null);
        }
        return null;
    }

    private sealed class AsyncLocalAgentContextAccessorFallback : IAgentContextAccessor
    {
        public AgentContext Current => AgentContext.Empty;
    }
}
