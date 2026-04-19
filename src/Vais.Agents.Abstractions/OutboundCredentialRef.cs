// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents;

/// <summary>
/// Declarative reference to a credential the agent uses to call external services
/// (APIs, MCP servers, A2A peers). Resolved at activation time by the host's
/// <see cref="IAgentIdentityProvider"/> — the manifest only carries the pointer
/// + type, never the credential value.
/// </summary>
/// <param name="Name">
/// Stable name tools / adapters use to look up this credential at invocation time.
/// E.g. an MCP server with <c>authRef: openai-api</c> matches a credential with
/// <c>Name: "openai-api"</c>.
/// </param>
/// <param name="Ref"><c>secret://</c> URI pointing at the credential store.</param>
/// <param name="Type">
/// Credential kind — <c>"bearer"</c>, <c>"basic"</c>, <c>"oauth2ClientCredentials"</c>,
/// <c>"apiKey"</c>, <c>"custom"</c>. Consumer-defined; the identity provider routes
/// on this value.
/// </param>
public sealed record OutboundCredentialRef(string Name, string Ref, string Type);
