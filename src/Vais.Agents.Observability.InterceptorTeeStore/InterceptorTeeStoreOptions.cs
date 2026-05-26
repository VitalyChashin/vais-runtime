// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Observability.InterceptorTeeStore;

/// <summary>Configuration for the Postgres-backed <see cref="PostgresInterceptorTeeStore"/>.</summary>
public sealed class InterceptorTeeStoreOptions
{
    /// <summary>Postgres connection string. Required when wiring the Postgres store.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Days to retain trajectory events. Older events are pruned on startup by
    /// <see cref="InterceptorTeeStoreInitializer"/>. Default 30 days. Mirrors the
    /// agent run store's retention semantics.
    /// </summary>
    public int RetentionDays { get; set; } = 30;
}
