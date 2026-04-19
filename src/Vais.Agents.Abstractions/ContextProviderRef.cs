// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents;

/// <summary>
/// Declarative reference to an <see cref="IContextProvider"/> bound to this agent.
/// Lands on <see cref="AgentManifest.ContextProviders"/>; the runtime resolves each
/// entry against the host's DI keyspace at agent activation time.
/// </summary>
/// <param name="Name">Provider name — matches a DI key registered by the host.</param>
/// <param name="Params">Provider-specific configuration (topK, collection names, embedding model, etc). Opaque to the manifest; interpreted by the provider's factory.</param>
public sealed record ContextProviderRef(string Name, JsonElement? Params = null);
