// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Observability.RunHealthStore;

/// <summary>
/// Options for the run-health signal store and its subscriber.
/// </summary>
public sealed class RunHealthStoreOptions
{
    /// <summary>Postgres connection string. Required when using <see cref="PostgresRunHealthStore"/>.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>How many days of signals to retain. Older rows are deleted on startup. Default 30.</summary>
    public int RetentionDays { get; set; } = 30;
}
