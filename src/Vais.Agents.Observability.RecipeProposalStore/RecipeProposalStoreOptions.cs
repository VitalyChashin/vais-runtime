// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Observability.RecipeProposalStore;

/// <summary>Configuration for the Postgres-backed <see cref="PostgresRecipeProposalStore"/>.</summary>
public sealed class RecipeProposalStoreOptions
{
    /// <summary>Postgres connection string. Required when wiring the Postgres store.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Days to retain decided proposals (Approved / Rejected / Superseded). Older decided
    /// proposals are pruned on startup; Pending proposals are never pruned automatically.
    /// Default 90 days.
    /// </summary>
    public int RetentionDays { get; set; } = 90;
}
