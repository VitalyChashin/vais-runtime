// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vais.Agents.Control;

namespace Vais.Agents.Runtime.Plugins.Container;

/// <summary>
/// In-process <see cref="IContainerPluginLifecycleManager"/> that persists manifests via
/// <see cref="IContainerPluginRegistry"/> and activates/stops the container via
/// <see cref="IContainerPluginHost"/>. Routes all mutating verbs through policy + audit middleware.
/// </summary>
public sealed class ContainerPluginLifecycleManager : IContainerPluginLifecycleManager
{
    private readonly IContainerPluginRegistry _registry;
    private readonly IContainerPluginHost _host;
    private readonly IAgentPolicyEngine _policy;
    private readonly IAuditLog _audit;
    private readonly IAgentContextAccessor _contextAccessor;
    private readonly ILogger<ContainerPluginLifecycleManager> _logger;

    /// <summary>Construct a manager. Registry and host are required; all other dependencies are optional.</summary>
    public ContainerPluginLifecycleManager(
        IContainerPluginRegistry registry,
        IContainerPluginHost host,
        IAgentPolicyEngine? policy = null,
        IAuditLog? audit = null,
        IAgentContextAccessor? contextAccessor = null,
        ILogger<ContainerPluginLifecycleManager>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(host);
        _registry = registry;
        _host = host;
        _policy = policy ?? NullAgentPolicyEngine.Instance;
        _audit = audit ?? NullAuditLog.Instance;
        _contextAccessor = contextAccessor ?? new AsyncLocalAgentContextAccessorFallback();
        _logger = logger ?? NullLogger<ContainerPluginLifecycleManager>.Instance;
    }

    /// <inheritdoc />
    public async ValueTask<ContainerPluginHandle> CreateAsync(
        ContainerPluginManifest manifest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var principal = SynthesizePrincipal();
        await GateAsync(PolicyOperation.ContainerPluginCreate, manifest.Id, manifest.Version, principal, ct).ConfigureAwait(false);

        string? errorType = null;
        try
        {
            var existing = await _registry.GetAsync(manifest.Id, manifest.Version, ct).ConfigureAwait(false);
            if (existing is not null)
                throw new ContainerPluginConflictException(manifest.Id, manifest.Version);

            await _registry.RegisterAsync(manifest, ct).ConfigureAwait(false);
            try
            {
                await _host.RegisterAsync(manifest, ct).ConfigureAwait(false);
            }
            catch (Exception hostEx)
            {
                _logger.LogError(hostEx, "Container plugin '{Id}' host activation failed; rolling back registry entry", manifest.Id);
                await _registry.RemoveAsync(manifest.Id, manifest.Version, ct).ConfigureAwait(false);
                throw;
            }

            return new ContainerPluginHandle(manifest.Id, manifest.Version);
        }
        catch (Exception ex)
        {
            errorType = ex.GetType().Name;
            throw;
        }
        finally
        {
            await AuditAsync(PolicyOperation.ContainerPluginCreate, manifest.Id, manifest.Version, principal, allowed: true, denyReason: null, errorType).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask<ContainerPluginHandle> UpdateAsync(
        ContainerPluginHandle handle, ContainerPluginManifest newManifest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(newManifest);
        var principal = SynthesizePrincipal();
        await GateAsync(PolicyOperation.ContainerPluginUpdate, handle.Id, handle.Version, principal, ct).ConfigureAwait(false);

        string? errorType = null;
        try
        {
            await _host.UnregisterAsync(handle.Id, ct).ConfigureAwait(false);
            await _registry.RegisterAsync(newManifest, ct).ConfigureAwait(false);
            if (!string.Equals(handle.Version, newManifest.Version, StringComparison.Ordinal))
                await _registry.RemoveAsync(handle.Id, handle.Version, ct).ConfigureAwait(false);

            try
            {
                await _host.RegisterAsync(newManifest, ct).ConfigureAwait(false);
            }
            catch (Exception hostEx)
            {
                _logger.LogError(hostEx, "Container plugin '{Id}' host activation failed during update", newManifest.Id);
                throw;
            }

            return new ContainerPluginHandle(newManifest.Id, newManifest.Version);
        }
        catch (Exception ex)
        {
            errorType = ex.GetType().Name;
            throw;
        }
        finally
        {
            await AuditAsync(PolicyOperation.ContainerPluginUpdate, handle.Id, handle.Version, principal, allowed: true, denyReason: null, errorType).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask<ContainerPluginRuntimeStatus> QueryAsync(
        ContainerPluginHandle handle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var principal = SynthesizePrincipal();
        await GateAsync(PolicyOperation.ContainerPluginQuery, handle.Id, handle.Version, principal, ct).ConfigureAwait(false);

        try
        {
            var manifest = await _registry.GetAsync(handle.Id, handle.Version, ct).ConfigureAwait(false);
            if (manifest is null)
                throw new ContainerPluginHandleNotFoundException(handle.Id, handle.Version);

            var loaded = _host.LoadedPlugins.FirstOrDefault(p =>
                string.Equals(p.Name, handle.Id, StringComparison.OrdinalIgnoreCase));
            var topology = loaded?.Topology ?? manifest.Spec.Topology;

            return new ContainerPluginRuntimeStatus(handle, topology, DateTimeOffset.UtcNow);
        }
        finally
        {
            await AuditAsync(PolicyOperation.ContainerPluginQuery, handle.Id, handle.Version, principal, allowed: true, denyReason: null, errorType: null).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ContainerPluginManifest> ListAsync(
        string? labelPrefix = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var manifest in _registry.ListAsync(labelPrefix, ct).ConfigureAwait(false))
            yield return manifest;
    }

    /// <inheritdoc />
    public async ValueTask EvictAsync(ContainerPluginHandle handle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var principal = SynthesizePrincipal();
        await GateAsync(PolicyOperation.ContainerPluginEvict, handle.Id, handle.Version, principal, ct).ConfigureAwait(false);

        string? errorType = null;
        try
        {
            var manifest = await _registry.GetAsync(handle.Id, handle.Version, ct).ConfigureAwait(false);
            if (manifest is null)
                throw new ContainerPluginHandleNotFoundException(handle.Id, handle.Version);

            await _host.UnregisterAsync(handle.Id, ct).ConfigureAwait(false);
            await _registry.RemoveAsync(handle.Id, handle.Version, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            errorType = ex.GetType().Name;
            throw;
        }
        finally
        {
            await AuditAsync(PolicyOperation.ContainerPluginEvict, handle.Id, handle.Version, principal, allowed: true, denyReason: null, errorType).ConfigureAwait(false);
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async ValueTask GateAsync(PolicyOperation op, string pluginId, string version, AgentPrincipal? principal, CancellationToken ct)
    {
        var decision = await _policy.EvaluateAsync(op, manifest: null, principal, ct).ConfigureAwait(false);
        if (decision.IsAllowed) return;
        var reason = decision.Reason ?? "policy denied";
        await AuditAsync(op, pluginId, version, principal, allowed: false, denyReason: reason, errorType: null).ConfigureAwait(false);
        throw new AgentPolicyDeniedException(op, reason);
    }

    private async ValueTask AuditAsync(
        PolicyOperation op,
        string? pluginId,
        string? version,
        AgentPrincipal? principal,
        bool allowed,
        string? denyReason,
        string? errorType)
    {
        var entry = new AuditLogEntry(
            At: DateTimeOffset.UtcNow,
            Operation: op,
            AgentId: pluginId,
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
            return new AgentPrincipal(userId, ctx.TenantId, Scopes: null);
        return null;
    }

    private sealed class AsyncLocalAgentContextAccessorFallback : IAgentContextAccessor
    {
        public AgentContext Current => AgentContext.Empty;
    }
}
