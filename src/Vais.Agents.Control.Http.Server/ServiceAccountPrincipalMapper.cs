// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Security.Claims;

namespace Vais.Agents.Control.Http;

/// <summary>
/// <see cref="IPrincipalMapper"/> specialised for Kubernetes
/// ServiceAccount tokens. Extracts <see cref="AgentPrincipal.Id"/> from
/// the standard <c>sub</c> claim (shape
/// <c>system:serviceaccount:&lt;namespace&gt;:&lt;serviceaccount&gt;</c>)
/// and <see cref="AgentPrincipal.TenantId"/> from the SA's namespace.
/// </summary>
/// <remarks>
/// <para>
/// Opt in at the runtime host via <c>VAIS_SA_PRINCIPAL_MAPPER=true</c>
/// (or <c>VAIS_SA_PRINCIPAL_MAPPER=true</c> in Helm <c>auth.serviceAccountPrincipalMapper</c>),
/// or directly with
/// <c>services.AddSingleton&lt;IPrincipalMapper, ServiceAccountPrincipalMapper&gt;()</c>
/// before calling <see cref="AgentControlPlaneAuthServiceCollectionExtensions.AddAgentControlPlaneJwtAuth"/>.
/// The default <c>DefaultPrincipalMapper</c> maps <c>sub</c> → <c>Id</c>
/// without the namespace → <c>TenantId</c> split — sufficient for non-K8s
/// deployments.
/// </para>
/// <para>
/// Claims not matching the SA shape fall back to the default behaviour:
/// <c>Id</c> = <c>sub</c>, <c>TenantId</c> = <c>tenant_id</c> claim if
/// present, otherwise null.
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
