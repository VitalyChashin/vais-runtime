// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Vais.Agents.Control.InProcess;

/// <summary>
/// RBAC <see cref="IAgentPolicyEngine"/> (Plan B Phase 2). Authorizes mutating
/// control-plane verbs against an <see cref="AuthorRolesPolicy"/> (authored in the
/// ontology overlay) and the caller's JWT scopes (<see cref="AgentPrincipal.Scopes"/>).
/// A mutating verb is allowed when one of the principal's scopes names a role that
/// grants the (kind, action) being attempted; otherwise it is denied.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope of enforcement.</b> Only mutating <em>authoring</em> verbs
/// (create / update / delete / upsert per kind) are governed. Runtime and read verbs
/// (<see cref="PolicyOperation.Invoke"/>, <see cref="PolicyOperation.Signal"/>,
/// <see cref="PolicyOperation.Query"/>, graph run verbs, …) always pass — wiring this
/// engine never breaks agent invocation or status reads.
/// </para>
/// <para>
/// <b>Safe default.</b> When the engine is wired but the principal has no scope that
/// grants the attempted authoring action (including no scopes at all, or an empty
/// policy), the verb is denied. Leaving the engine unwired keeps the runtime's
/// allow-all <see cref="NullAgentPolicyEngine"/> default (RBAC is opt-in).
/// </para>
/// </remarks>
public sealed class AuthorRolesPolicyEngine : IAgentPolicyEngine
{
    private readonly AuthorRolesPolicy _policy;

    /// <summary>Construct over the author-roles policy (typically the overlay's authorRoles).</summary>
    public AuthorRolesPolicyEngine(AuthorRolesPolicy policy)
    {
        _policy = policy ?? AuthorRolesPolicy.Empty;
    }

    /// <inheritdoc />
    public ValueTask<PolicyDecision> EvaluateAsync(
        PolicyOperation operation,
        AgentManifest? manifest,
        AgentPrincipal? principal,
        CancellationToken cancellationToken = default)
    {
        if (!AuthoringActions.TryMap(operation, out var kind, out var action))
        {
            // Non-authoring verb (runtime / read) — not RBAC's concern.
            return new ValueTask<PolicyDecision>(PolicyDecision.Allow);
        }

        var scopes = principal?.Scopes;
        if (scopes is not null && _policy.Roles is { Count: > 0 } roles)
        {
            foreach (var scope in scopes)
            {
                if (roles.TryGetValue(scope, out var role) && Permits(role, kind, action))
                {
                    return new ValueTask<PolicyDecision>(PolicyDecision.Allow);
                }
            }
        }

        return new ValueTask<PolicyDecision>(PolicyDecision.Deny(
            $"principal lacks an author role permitting '{action}' on kind '{kind}'"));
    }

    private static bool Permits(AuthorRole role, string kind, string action)
    {
        if (role.Permissions is null || !role.Permissions.TryGetValue(kind, out var actions) || actions is null)
        {
            return false;
        }
        foreach (var a in actions)
        {
            if (a == "*" || string.Equals(a, action, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}

/// <summary>
/// Maps a <see cref="PolicyOperation"/> to the (kind, action) pair the RBAC engine
/// authorizes. Returns <see langword="false"/> for non-authoring verbs (runtime / read).
/// </summary>
internal static class AuthoringActions
{
    internal const string Write = "write";
    internal const string Delete = "delete";

    public static bool TryMap(PolicyOperation op, out string kind, out string action)
    {
        switch (op)
        {
            case PolicyOperation.Create:
            case PolicyOperation.Update:
                kind = "Agent"; action = Write; return true;
            case PolicyOperation.Evict:
                kind = "Agent"; action = Delete; return true;

            case PolicyOperation.GraphCreate:
            case PolicyOperation.GraphUpdate:
                kind = "AgentGraph"; action = Write; return true;
            case PolicyOperation.GraphEvict:
                kind = "AgentGraph"; action = Delete; return true;

            case PolicyOperation.LlmGatewayConfigCreate:
            case PolicyOperation.LlmGatewayConfigUpdate:
                kind = "LlmGatewayConfig"; action = Write; return true;
            case PolicyOperation.LlmGatewayConfigEvict:
                kind = "LlmGatewayConfig"; action = Delete; return true;

            case PolicyOperation.McpGatewayConfigCreate:
            case PolicyOperation.McpGatewayConfigUpdate:
                kind = "McpGatewayConfig"; action = Write; return true;
            case PolicyOperation.McpGatewayConfigEvict:
                kind = "McpGatewayConfig"; action = Delete; return true;

            case PolicyOperation.McpServerCreate:
            case PolicyOperation.McpServerUpdate:
                kind = "McpServer"; action = Write; return true;
            case PolicyOperation.McpServerEvict:
                kind = "McpServer"; action = Delete; return true;

            case PolicyOperation.ContainerPluginCreate:
            case PolicyOperation.ContainerPluginUpdate:
                kind = "ContainerPlugin"; action = Write; return true;
            case PolicyOperation.ContainerPluginEvict:
                kind = "ContainerPlugin"; action = Delete; return true;

            case PolicyOperation.EvalSuiteUpsert:
                kind = "EvalSuite"; action = Write; return true;
            case PolicyOperation.EvalSuiteEvict:
                kind = "EvalSuite"; action = Delete; return true;

            case PolicyOperation.ExtensionCreate:
            case PolicyOperation.ExtensionUpdate:
                kind = "Extension"; action = Write; return true;
            case PolicyOperation.ExtensionEvict:
                kind = "Extension"; action = Delete; return true;

            default:
                kind = string.Empty;
                action = string.Empty;
                return false;
        }
    }
}

/// <summary>DI helper to opt into the RBAC author-roles policy engine.</summary>
public static class AuthorRolesPolicyServiceCollectionExtensions
{
    /// <summary>
    /// Replace the registered <see cref="IAgentPolicyEngine"/> with an
    /// <see cref="AuthorRolesPolicyEngine"/> driven by <paramref name="policy"/>
    /// (typically the ontology overlay's <c>authorRoles</c>). Opt-in — without this call
    /// the runtime keeps the allow-all <see cref="NullAgentPolicyEngine"/> default.
    /// </summary>
    public static IServiceCollection AddAuthorRolesPolicy(this IServiceCollection services, AuthorRolesPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(policy);
        services.Replace(ServiceDescriptor.Singleton<IAgentPolicyEngine>(new AuthorRolesPolicyEngine(policy)));
        return services;
    }
}
