// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Observability.AgentRunStore;

/// <summary>Configuration options for the agent run store.</summary>
public sealed class AgentRunStoreOptions
{
    /// <summary>Postgres connection string. Required when using <see cref="PostgresAgentRunStore"/>.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Number of days to retain agent run history. Runs older than this are pruned on startup.
    /// Default is 30 days.
    /// </summary>
    public int RetentionDays { get; set; } = 30;
}
