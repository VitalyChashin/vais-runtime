// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Vais.Agents;
using Vais.Agents.Core;

// -----------------------------------------------------------------------------
// PromptComposer — demonstrates AggregatingSystemPromptComposer + two
// ISystemPromptContributor implementations at different priorities. Drives one
// turn through a scripted fake provider so the composed system prompt is
// observable in the request passed to CompleteAsync.
// -----------------------------------------------------------------------------

var composer = new AggregatingSystemPromptComposer(new ISystemPromptContributor[]
{
    new PersonaContributor("You are a calm, concise support assistant."),  // priority 0
    new TenantPolicyContributor(                                            // priority 10
        tenantId: "acme",
        policy: "Never discuss pricing; route to sales@example.com."),
});

// Set ambient context so the tenant-policy contributor fires.
var accessor = new AsyncLocalAgentContextAccessor();
using var ambient = accessor.Push(new AgentContext(TenantId: "acme"));

var provider = new RecordingFakeProvider();

var agent = new StatefulAiAgent(
    provider,
    new StatefulAgentOptions
    {
        SystemPromptComposer = composer,
        ContextAccessor = accessor,
    });

var reply = await agent.AskAsync("How much is the service?");

Console.WriteLine("=== Composed system prompt seen by the provider ===");
Console.WriteLine(provider.LastRequest!.SystemPrompt);
Console.WriteLine();
Console.WriteLine("=== Agent reply (from the fake provider) ===");
Console.WriteLine(reply);

// ---- Scripted fake provider that records the last request ----
sealed class RecordingFakeProvider : ICompletionProvider
{
    public CompletionRequest? LastRequest { get; private set; }
    public string ProviderName => "recording-fake";
    public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken ct = default)
    {
        LastRequest = request;
        return Task.FromResult(new CompletionResponse(
            "Acknowledged. I'll route pricing queries to sales@example.com.",
            ModelId: "fake-model"));
    }
}

// ---- Contributors ----
sealed class PersonaContributor(string persona) : ISystemPromptContributor
{
    public int Priority => 0;
    public ValueTask<string?> ContributeAsync(AgentContext context, CancellationToken ct = default)
        => ValueTask.FromResult<string?>(persona);
}

sealed class TenantPolicyContributor(string tenantId, string policy) : ISystemPromptContributor
{
    public int Priority => 10;
    public ValueTask<string?> ContributeAsync(AgentContext context, CancellationToken ct = default)
        => context.TenantId == tenantId
            ? ValueTask.FromResult<string?>($"Tenant policy ({tenantId}): {policy}")
            : ValueTask.FromResult<string?>(null);
}
