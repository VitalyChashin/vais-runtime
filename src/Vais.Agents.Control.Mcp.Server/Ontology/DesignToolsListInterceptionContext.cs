// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.Mcp.Server.Ontology;

/// <summary>
/// Substrate context for the design-tools <c>tools/list</c> interception chain. Carries the
/// ambient agent context; no transport-specific payload — the chain produces a
/// <c>ListToolsResult</c> outcome, starting from a baseline of the read-only design verbs.
/// </summary>
internal sealed class DesignToolsListInterceptionContext : InterceptionContext;
