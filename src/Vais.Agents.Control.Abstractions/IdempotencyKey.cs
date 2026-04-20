// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control;

/// <summary>
/// 4-tuple scope for an idempotency-key lookup. Matches the Stripe / IETF
/// convention where keys are scoped by (tenant, method, path, user-supplied key)
/// so the same key value in different tenants or on different endpoints never
/// collides.
/// </summary>
/// <param name="TenantId">
/// Tenant scope derived from the authenticated <see cref="AgentPrincipal.TenantId"/>.
/// Anonymous (no JWT) requests pass <c>null</c>; anonymous keys share a single
/// scope, which is a documented limitation — production deployments should
/// authenticate.
/// </param>
/// <param name="Method">HTTP method (<c>POST</c> / <c>PATCH</c> / <c>DELETE</c>). GETs aren't deduped.</param>
/// <param name="Path">Request path excluding query string. Scope is path-literal, not route-pattern.</param>
/// <param name="Key">Client-supplied <c>Idempotency-Key</c> header value. Length capped (default 255).</param>
public readonly record struct IdempotencyKey(
    string? TenantId,
    string Method,
    string Path,
    string Key);
