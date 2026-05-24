// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Control;

/// <summary>
/// Deployment-local RBAC map authored in the ontology overlay (Plan B). Each key is
/// a JWT scope string the caller's bearer token carries (the <c>scope</c>/<c>scp</c>
/// claim, parsed onto <see cref="AgentPrincipal.Scopes"/>); the value is the set of
/// (kind, action) authoring permissions that scope grants. Consumed by the RBAC
/// <c>AuthorRolesPolicyEngine</c> to allow or deny mutating control-plane verbs.
/// </summary>
/// <remarks>
/// The mechanism is OSS; the role <em>content</em> stays deployment-local (the
/// overlay file is not checked into <c>agentic/</c>). When no policy is wired the
/// runtime keeps its allow-all default — RBAC is opt-in.
/// </remarks>
public sealed record AuthorRolesPolicy
{
    /// <summary>JWT scope string → granted role. Null / empty = no roles configured.</summary>
    public IReadOnlyDictionary<string, AuthorRole>? Roles { get; init; }

    /// <summary>An empty policy that grants nothing.</summary>
    public static readonly AuthorRolesPolicy Empty = new();

    /// <summary>True when no roles are defined.</summary>
    public bool IsEmpty => Roles is null || Roles.Count == 0;
}

/// <summary>A single author role: per-kind authoring permissions.</summary>
public sealed record AuthorRole
{
    /// <summary>
    /// Manifest kind name (e.g. <c>Agent</c>, <c>McpGatewayConfig</c>) → permitted action
    /// tokens. Recognized actions: <c>write</c> (create / update / upsert), <c>delete</c>,
    /// and <c>*</c> (all actions on that kind). Matching is case-insensitive.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? Permissions { get; init; }
}
