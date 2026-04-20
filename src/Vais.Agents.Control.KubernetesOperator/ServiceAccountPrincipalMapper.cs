// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Security.Claims;

namespace Vais.Agents.Control.Kubernetes;

/// <summary>
/// <see cref="IPrincipalMapper"/> specialised for Kubernetes
/// ServiceAccount tokens. Extracts <see cref="AgentPrincipal.Id"/> from
/// the standard <c>sub</c> claim (shape
/// <c>system:serviceaccount:&lt;namespace&gt;:&lt;serviceaccount&gt;</c>)
/// and <see cref="AgentPrincipal.TenantId"/> from the SA's namespace.
/// </summary>
/// <remarks>
/// <para>
/// Optional: consumers opt in via
/// <c>services.AddSingleton&lt;IPrincipalMapper, ServiceAccountPrincipalMapper&gt;()</c>
/// on the runtime side. The default <c>DefaultPrincipalMapper</c>
/// shipped by v0.6 maps <c>sub</c> → <c>Id</c> without the namespace →
/// <c>TenantId</c> split — it's sufficient for non-K8s deployments.
/// </para>
/// <para>
/// Claims not matching the SA shape fall back to the shipped-v0.6
/// default behaviour: <c>Id</c> = <c>sub</c> (or empty), <c>TenantId</c>
/// = <c>tenant_id</c> claim if present, otherwise null. Keeps behaviour
/// predictable for mixed-auth scenarios (a runtime serving both SA and
/// human OIDC tokens).
/// </para>
/// </remarks>
public sealed class ServiceAccountPrincipalMapper : IPrincipalMapper
{
    private const string ServiceAccountPrefix = "system:serviceaccount:";

    /// <inheritdoc />
    public AgentPrincipal? Map(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);

        var sub = (user.FindFirst("sub") ?? user.FindFirst(ClaimTypes.NameIdentifier))?.Value;
        if (string.IsNullOrWhiteSpace(sub))
        {
            return null;
        }

        if (sub.StartsWith(ServiceAccountPrefix, StringComparison.Ordinal))
        {
            var rest = sub[ServiceAccountPrefix.Length..];
            var colon = rest.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                var namespaceName = rest[..colon];
                return new AgentPrincipal(
                    Id: sub,
                    TenantId: namespaceName,
                    Scopes: ExtractScopes(user));
            }
        }

        return new AgentPrincipal(
            Id: sub,
            TenantId: user.FindFirst("tenant_id")?.Value,
            Scopes: ExtractScopes(user));
    }

    private static IReadOnlyList<string>? ExtractScopes(ClaimsPrincipal user)
    {
        var scopeClaim = user.FindFirst("scope")?.Value;
        if (string.IsNullOrWhiteSpace(scopeClaim))
        {
            return null;
        }
        return scopeClaim.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
