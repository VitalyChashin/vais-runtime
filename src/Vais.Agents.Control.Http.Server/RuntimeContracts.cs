// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.Http;

/// <summary>
/// Serializable descriptor of a single remote runtime reachable from this host.
/// Contains only the URL and identity mode — credentials are intentionally excluded.
/// </summary>
public sealed record RuntimeInfo(string Url, string IdentityMode);

/// <summary>Response body for <c>GET /v1/runtimes</c>.</summary>
public sealed record RuntimeListResponse(IReadOnlyList<RuntimeInfo> Items);
