// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Security.Claims;

namespace Vais.Agents.Control.Http;

/// <summary>
/// OIDC-convention <see cref="IPrincipalMapper"/> — reads the standard claims
/// <c>sub</c> (principal id), <c>tenant_id</c> / <c>tid</c> (tenant), and
/// <c>scope</c> (space-separated OAuth scopes) from the <see cref="ClaimsPrincipal"/>.
/// Consumers with custom JWT shapes replace via DI.
/// </summary>
public sealed class DefaultPrincipalMapper : IPrincipalMapper
{
    /// <inheritdoc />
    public AgentPrincipal? Map(ClaimsPrincipal claimsPrincipal)
    {
        ArgumentNullException.ThrowIfNull(claimsPrincipal);
        if (claimsPrincipal.Identity is null || !claimsPrincipal.Identity.IsAuthenticated)
        {
            return null;
        }

        var id = claimsPrincipal.FindFirst("sub")?.Value
              ?? claimsPrincipal.FindFirst(ClaimTypes.NameIdentifier)?.Value
              ?? claimsPrincipal.Identity.Name;
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        var tenant = claimsPrincipal.FindFirst("tenant_id")?.Value
                  ?? claimsPrincipal.FindFirst("tid")?.Value;

        var scopeRaw = claimsPrincipal.FindFirst("scope")?.Value
                    ?? claimsPrincipal.FindFirst("scp")?.Value;
        IReadOnlyList<string>? scopes = null;
        if (!string.IsNullOrWhiteSpace(scopeRaw))
        {
            scopes = scopeRaw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        return new AgentPrincipal(id, tenant, scopes);
    }
}
