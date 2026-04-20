// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control.Kubernetes;

/// <summary>
/// Factory helpers for building the three canonical condition types
/// surfaced on <see cref="AgentStatus.Conditions"/>. Centralises type
/// strings + status strings + common reason codes so tests and
/// production code share the same vocabulary.
/// </summary>
internal static class AgentConditions
{
    /// <summary>Reconcile pass succeeded or is in progress.</summary>
    public const string ReadyType = "Ready";

    /// <summary>Runtime state matches the desired spec.</summary>
    public const string SyncedType = "Synced";

    /// <summary>Last upsert's manifest passed runtime validation + secret-ref resolution.</summary>
    public const string ManifestValidType = "ManifestValid";

    /// <summary>Condition status: <c>"True"</c>.</summary>
    public const string StatusTrue = "True";

    /// <summary>Condition status: <c>"False"</c>.</summary>
    public const string StatusFalse = "False";

    /// <summary>Condition status: <c>"Unknown"</c>.</summary>
    public const string StatusUnknown = "Unknown";

    public static AgentCondition Ready(string status, string reason, string message, DateTimeOffset at, long generation) =>
        new(ReadyType, status, reason, message, at, generation);

    public static AgentCondition Synced(string status, string reason, string message, DateTimeOffset at, long generation) =>
        new(SyncedType, status, reason, message, at, generation);

    public static AgentCondition ManifestValid(string status, string reason, string message, DateTimeOffset at, long generation) =>
        new(ManifestValidType, status, reason, message, at, generation);
}
