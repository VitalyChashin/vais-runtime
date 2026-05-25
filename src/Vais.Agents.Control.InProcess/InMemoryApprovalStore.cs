// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Vais.Agents.Control.InProcess;

/// <summary>
/// In-memory <see cref="IApprovalStore"/> — correct for a single host (Docker standalone,
/// the primary co-tenant target). NOT cluster-safe: a clustered deployment must register
/// the Orleans grain-backed store so pending approvals survive a silo restart and are
/// visible from any silo (P1, documented scaling gap).
/// </summary>
public sealed class InMemoryApprovalStore : IApprovalStore
{
    private readonly ConcurrentDictionary<string, ApprovalRequest> _byId = new();

    /// <inheritdoc />
    public ValueTask<ApprovalRequest?> FindApprovedAsync(string kind, string name, string manifestHash, CancellationToken ct = default)
    {
        foreach (var r in _byId.Values)
        {
            if (r.Status == ApprovalStatus.Approved && Matches(r, kind, name, manifestHash))
            {
                return ValueTask.FromResult<ApprovalRequest?>(r);
            }
        }
        return ValueTask.FromResult<ApprovalRequest?>(null);
    }

    /// <inheritdoc />
    public ValueTask<ApprovalRequest> CreatePendingAsync(string kind, string name, string manifestHash, string requestedBy, CancellationToken ct = default)
    {
        // Idempotent: re-applying the same held manifest returns the existing pending request.
        foreach (var r in _byId.Values)
        {
            if (r.Status == ApprovalStatus.Pending && Matches(r, kind, name, manifestHash))
            {
                return ValueTask.FromResult(r);
            }
        }

        var created = new ApprovalRequest(
            RequestId: Guid.NewGuid().ToString("N"),
            Kind: kind,
            Name: name,
            ManifestHash: manifestHash,
            RequestedBy: requestedBy,
            RequestedAt: DateTimeOffset.UtcNow,
            Status: ApprovalStatus.Pending,
            DecidedBy: null,
            DecidedAt: null);
        _byId[created.RequestId] = created;
        return ValueTask.FromResult(created);
    }

    /// <inheritdoc />
    public ValueTask<ApprovalRequest?> GetAsync(string requestId, CancellationToken ct = default)
        => ValueTask.FromResult(_byId.TryGetValue(requestId, out var r) ? r : null);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ApprovalRequest>> ListAsync(ApprovalStatus? status = null, CancellationToken ct = default)
    {
        IReadOnlyList<ApprovalRequest> items = _byId.Values
            .Where(r => status is null || r.Status == status)
            .OrderBy(r => r.RequestedAt)
            .ToList();
        return ValueTask.FromResult(items);
    }

    /// <inheritdoc />
    public ValueTask<ApprovalRequest?> DecideAsync(string requestId, bool approve, string decidedBy, CancellationToken ct = default)
    {
        if (!_byId.TryGetValue(requestId, out var existing) || existing.Status != ApprovalStatus.Pending)
        {
            return ValueTask.FromResult<ApprovalRequest?>(null);
        }
        var decided = existing with
        {
            Status = approve ? ApprovalStatus.Approved : ApprovalStatus.Rejected,
            DecidedBy = decidedBy,
            DecidedAt = DateTimeOffset.UtcNow,
        };
        _byId[requestId] = decided;
        return ValueTask.FromResult<ApprovalRequest?>(decided);
    }

    private static bool Matches(ApprovalRequest r, string kind, string name, string hash)
        => string.Equals(r.Kind, kind, StringComparison.Ordinal)
        && string.Equals(r.Name, name, StringComparison.Ordinal)
        && string.Equals(r.ManifestHash, hash, StringComparison.Ordinal);
}

/// <summary>Stable content hash for approval binding — SHA-256 hex of the canonical manifest.</summary>
public static class ApprovalHash
{
    /// <summary>Compute the lowercase hex SHA-256 of <paramref name="canonical"/> (UTF-8).</summary>
    public static string Compute(string canonical)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(bytes);
    }
}

/// <summary>
/// Default <see cref="IApprovalGate"/>. Holds mutations of configured high-risk kinds until a
/// matching <see cref="ApprovalStatus.Approved"/> request exists in the <see cref="IApprovalStore"/>;
/// otherwise records a pending request and throws <see cref="ApprovalRequiredException"/>.
/// </summary>
public sealed class ApprovalGate : IApprovalGate
{
    /// <summary>Kinds that run code and therefore require approval by default.</summary>
    public static readonly IReadOnlySet<string> DefaultHighRiskKinds =
        new HashSet<string>(StringComparer.Ordinal) { "ContainerPlugin", "Extension", "Plugin" };

    private readonly IApprovalStore _store;
    private readonly IReadOnlySet<string> _highRiskKinds;

    /// <summary>Construct over a store and an optional high-risk kind set (defaults to ContainerPlugin + Extension).</summary>
    public ApprovalGate(IApprovalStore store, IReadOnlySet<string>? highRiskKinds = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _highRiskKinds = highRiskKinds ?? DefaultHighRiskKinds;
    }

    /// <inheritdoc />
    public async ValueTask EnsureApprovedAsync(string kind, string name, string manifestCanonical, string requestedBy, CancellationToken ct = default)
    {
        if (!_highRiskKinds.Contains(kind))
        {
            return;
        }

        var hash = ApprovalHash.Compute(manifestCanonical);
        var approved = await _store.FindApprovedAsync(kind, name, hash, ct).ConfigureAwait(false);
        if (approved is not null)
        {
            return; // a matching approval exists — proceed with the mutation
        }

        var pending = await _store.CreatePendingAsync(kind, name, hash, requestedBy, ct).ConfigureAwait(false);
        throw new ApprovalRequiredException(kind, name, pending.RequestId);
    }
}

/// <summary>DI helpers to opt into the approval subsystem (single-host in-memory store).</summary>
public static class ApprovalServiceCollectionExtensions
{
    /// <summary>
    /// Register the in-memory <see cref="IApprovalStore"/> + the default <see cref="IApprovalGate"/>.
    /// Opt-in — without this call no approval is enforced and high-risk mutations proceed.
    /// A clustered host registers an Orleans-backed <see cref="IApprovalStore"/> instead, then
    /// adds the gate via <see cref="AddApprovalGate"/>.
    /// </summary>
    public static IServiceCollection AddInMemoryApprovals(this IServiceCollection services, IReadOnlySet<string>? highRiskKinds = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IApprovalStore, InMemoryApprovalStore>();
        return services.AddApprovalGate(highRiskKinds);
    }

    /// <summary>Register the default <see cref="IApprovalGate"/> over an already-registered <see cref="IApprovalStore"/>.</summary>
    public static IServiceCollection AddApprovalGate(this IServiceCollection services, IReadOnlySet<string>? highRiskKinds = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IApprovalGate>(sp => new ApprovalGate(sp.GetRequiredService<IApprovalStore>(), highRiskKinds));
        return services;
    }
}
