// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Observability.RunStore;

/// <summary>Configuration options for the run store.</summary>
public sealed class RunStoreOptions
{
    /// <summary>Postgres connection string. Required when using <see cref="PostgresRunStore"/>.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Number of days to retain run history. Runs older than this are pruned on startup.
    /// Default is 30 days.
    /// </summary>
    public int RetentionDays { get; set; } = 30;
}
