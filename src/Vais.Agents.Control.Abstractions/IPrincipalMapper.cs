// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Security.Claims;

namespace Vais.Agents.Control;

/// <summary>
/// Maps an inbound authentication principal (<see cref="ClaimsPrincipal"/> from
/// ASP.NET Core JWT middleware or equivalent) to the neutral
/// <see cref="AgentPrincipal"/> the lifecycle manager + policy engine consume.
/// </summary>
/// <remarks>
/// <para>
/// The default implementation in <c>Vais.Agents.Control.Http.Server</c> reads
/// <c>sub</c> / <c>tenant_id</c> / <c>scope</c> claims per OIDC conventions.
/// Consumers with custom JWT shapes supply their own <see cref="IPrincipalMapper"/>.
/// </para>
/// </remarks>
public interface IPrincipalMapper
{
    /// <summary>
    /// Translate <paramref name="claimsPrincipal"/> into an <see cref="AgentPrincipal"/>,
    /// or null when the caller is anonymous / unauthenticated.
    /// </summary>
    AgentPrincipal? Map(ClaimsPrincipal claimsPrincipal);
}
