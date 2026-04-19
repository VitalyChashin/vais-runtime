// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Vais.Agents;
using Vais.Agents.Core;

// -----------------------------------------------------------------------------
// AgentManifestAndRegistry — builds a full AgentManifest, registers via
// InMemoryAgentRegistry, lists by label prefix, fetches latest-lexicographic
// version.
// -----------------------------------------------------------------------------

var registry = new InMemoryAgentRegistry();

registry.Register(new AgentManifest(
    Id: "support-agent",
    Version: "1.0.0",
    Handler: new AgentHandlerRef(TypeName: "MyApp.SupportAgent", AssemblyName: "MyApp"),
    Protocols: new[] { new ProtocolBinding(Kind: "Http", Endpoint: "/agents/support") },
    Tools: new[]
    {
        new ToolRef(Name: "lookup_order"),
        new ToolRef(Name: "send_email", Source: "mcp:email-server"),
    },
    Memory: new MemoryRef(Provider: "Redis", ConnectionName: "shared-redis"),
    Identity: new IdentityRef(InboundAuth: "oauth:keycloak", OutboundCredentials: "kv:support-agent/*"),
    Autoscaling: new AutoscalingSpec(MinReplicas: 1, MaxReplicas: 10, Target: "concurrent-requests"),
    Description: "Customer-support agent v1.0.",
    Labels: new Dictionary<string, string>
    {
        ["env"] = "staging",
        ["team"] = "customer-facing",
    }));

// Register a newer version of the same agent.
registry.Register(new AgentManifest(
    Id: "support-agent",
    Version: "1.1.0",
    Handler: new AgentHandlerRef(TypeName: "MyApp.SupportAgent", AssemblyName: "MyApp"),
    Protocols: new[] { new ProtocolBinding(Kind: "Http", Endpoint: "/agents/support") },
    Tools: new[] { new ToolRef(Name: "lookup_order"), new ToolRef(Name: "send_email") },
    Labels: new Dictionary<string, string>
    {
        ["env"] = "staging",
        ["team"] = "customer-facing",
    }));

registry.Register(new AgentManifest(
    Id: "sales-bot",
    Version: "2.0.0",
    Handler: new AgentHandlerRef(TypeName: "MyApp.SalesBot"),
    Protocols: new[] { new ProtocolBinding(Kind: "A2A") },
    Tools: new[] { new ToolRef(Name: "quote_price") },
    Labels: new Dictionary<string, string>
    {
        ["env"] = "production",
        ["team"] = "revenue",
    }));

// Latest-lexicographic: null version → 1.1.0
var latest = await registry.GetAsync("support-agent");
Console.WriteLine($"support-agent (latest) → v{latest!.Version}");

// Specific version:
var pinned = await registry.GetAsync("support-agent", version: "1.0.0");
Console.WriteLine($"support-agent (1.0.0)  → Handler {pinned!.Handler.TypeName}");

// List filtered by label-prefix (format is "key:value").
Console.WriteLine("=== manifests tagged env:staging ===");
await foreach (var m in registry.ListAsync(labelPrefix: "env:staging"))
    Console.WriteLine($"  {m.Id} v{m.Version} — {m.Description ?? "(no description)"}");

Console.WriteLine();
Console.WriteLine("=== all manifests ===");
await foreach (var m in registry.ListAsync())
    Console.WriteLine($"  {m.Id} v{m.Version} — {string.Join(", ", m.Labels?.Select(l => $"{l.Key}={l.Value}") ?? Array.Empty<string>())}");
