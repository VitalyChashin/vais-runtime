// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Vais.Agents.Control;

namespace Vais.Agents.Control.Http;

/// <summary>Response body for <c>GET /v1/diagnostics/spans</c>.</summary>
public sealed record DiagSpanListResponse(IReadOnlyList<DiagSpanRecord> Items);

/// <summary>Response body for <c>GET /v1/diagnostics/filter-status</c>.</summary>
public sealed record FilterStatusResponse(IReadOnlyList<FilterCallEntry> Calls, long TotalCalls);
