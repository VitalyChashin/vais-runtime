// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net.Http.Headers;
using Vais.Agents.Control.Http;

namespace Vais.Agents.Cli;

/// <summary>
/// Builds a configured <see cref="IAgentControlPlaneClient"/> from a
/// resolved config context + optional per-command token override.
/// Transient per CLI invocation — a fresh <see cref="HttpClient"/> is
/// created and disposed when the CLI process exits. No pooling needed
/// for short-lived CLI processes.
/// </summary>
public static class ClientFactory
{
    /// <summary>
    /// Resolve a client against <paramref name="config"/>'s active
    /// context (or <paramref name="contextOverride"/> when supplied).
    /// Returns the concrete <see cref="AgentControlPlaneClient"/>
    /// so callers can reach the v0.11 idempotency-key overloads + v0.12
    /// streaming methods; the returned value implements
    /// <see cref="IAgentControlPlaneClient"/>.
    /// </summary>
    /// <param name="config">Loaded <c>~/.vais/config.yaml</c> contents.</param>
    /// <param name="contextOverride">Optional explicit context name (e.g. from a <c>--context</c> flag); falls back to <see cref="VaisCliConfig.CurrentContext"/>.</param>
    /// <param name="tokenFlag">Optional <c>--token</c> override; wins over env var + context user.</param>
    /// <returns>A new <see cref="AgentControlPlaneClient"/>. Caller disposes the underlying <see cref="HttpClient"/> via <see cref="IDisposable"/>.</returns>
    /// <exception cref="InvalidOperationException">No context selected, cluster missing, or server URL blank.</exception>
    public static AgentControlPlaneClient Create(
        VaisCliConfig config,
        string? contextOverride = null,
        string? tokenFlag = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        var contextName = contextOverride ?? config.CurrentContext;
        if (string.IsNullOrWhiteSpace(contextName))
        {
            throw new InvalidOperationException(
                "No context selected. Run `vais config use-context <name>` or pass --context.");
        }

        var context = VaisConfigFile.FindContext(config, contextName)
            ?? throw new InvalidOperationException($"Context '{contextName}' not found in config.");

        var cluster = VaisConfigFile.FindCluster(config, context.Cluster)
            ?? throw new InvalidOperationException($"Cluster '{context.Cluster}' referenced by context '{contextName}' is missing.");

        if (string.IsNullOrWhiteSpace(cluster.Server))
        {
            throw new InvalidOperationException($"Cluster '{cluster.Name}' has an empty server URL.");
        }

        var user = VaisConfigFile.FindUser(config, context.User);
        var token = TokenResolver.Resolve(tokenFlag, user);

        var handler = new HttpClientHandler();
        if (cluster.InsecureSkipTlsVerify)
        {
            handler.ServerCertificateCustomValidationCallback = static (_, _, _, _) => true;
        }

        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(cluster.Server, UriKind.Absolute),
        };
        if (!string.IsNullOrWhiteSpace(token))
        {
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return new AgentControlPlaneClient(httpClient);
    }
}
