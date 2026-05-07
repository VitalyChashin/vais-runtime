// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

// A2AInterruptResumeOrleans — durable A2A interrupt/resume backed by Orleans.
//
// Run: dotnet run --project samples/A2AInterruptResumeOrleans
// Env: none (deterministic, scripted provider, no API key, no Docker)
// Docs: docs/guides/host-agents-as-an-a2a-endpoint.md
//
// Boots an in-process Orleans silo and ASP.NET Core server, then runs:
//   1. fresh call   — agent requests tool approval → guardrail interrupts
//                     → A2A handler creates Task(input-required) in OrleansTaskStore
//   2. resume call  — client sends message with TaskId set
//                     → A2A handler resumes → Task(completed)
//
// OrleansTaskStore (backed by IA2ATaskGrain) keeps A2A task state in Orleans grain
// storage, so input-required tasks survive silo restarts — unlike InMemoryTaskStore.

using System.Text.Json;
using A2A;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Hosting;
using Vais.Agents;
using Vais.Agents.Control;
using Vais.Agents.Control.InProcess;
using Vais.Agents.Core;
using Vais.Agents.Hosting.InMemory;
using Vais.Agents.Hosting.Orleans;
using Vais.Agents.Protocols.A2A.Server;

var port      = FindFreePort();
var serverUrl = $"http://127.0.0.1:{port}";

// --- build the server ---
var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.UseUrls(serverUrl);

// Orleans silo: in-memory grain storage — no external deps.
builder.Host.UseOrleans(silo =>
{
    silo.UseLocalhostClustering()
        .AddMemoryGrainStorage("vais.agents")
        .AddMemoryGrainStorage("PubSubStore")
        .AddMemoryStreams("vais.agents.events");
});

// Agent: scripted provider + approval tool + guardrail that interrupts on first tool call.
var provider  = new InterruptOnceProvider();
var registry  = new InMemoryAgentRegistry();
var runtime   = new InMemoryAgentRuntime(
    provider,
    optionsFactory: id => new StatefulAgentOptions
    {
        AgentName    = id,
        ToolRegistry = new SingletonRegistry(new ApproveActionTool()),
        ToolGuardrails = [new ApprovalGuardrail()],
    });
var lifecycle = new AgentLifecycleManager(registry, runtime);

await lifecycle.CreateAsync(
    new AgentManifest(
        Id:          "approver",
        Version:     "1.0",
        Handler:     new AgentHandlerRef("declarative"),
        Protocols:   [],
        Tools:       [],
        Description: "Executes high-value actions with human-in-the-loop approval."),
    CancellationToken.None);

builder.Services.AddSingleton<IAgentRegistry>(registry);
builder.Services.AddSingleton<IAgentLifecycleManager>(lifecycle);

// Register OrleansTaskStore BEFORE AddA2AAgentServer so TryAddSingleton keeps it.
// IGrainFactory is resolved lazily — the silo must be started before first use.
builder.Services.AddSingleton<ITaskStore, OrleansTaskStore>(
    sp => new OrleansTaskStore(sp.GetRequiredService<IGrainFactory>()));

builder.Services.AddA2AAgentServer();

var app = builder.Build();
app.MapA2AAgentServer(serverUrl);
await app.StartAsync();
Console.WriteLine($"Server: {serverUrl}/agents/approver");
Console.WriteLine();

// --- run the client ---
var client = new A2AClient(new Uri($"{serverUrl}/agents/approver"));

// ── step 1: fresh message — triggers interrupt ──
var msg1 = new Message { Role = Role.User, MessageId = Guid.NewGuid().ToString("N") };
msg1.Parts.Add(Part.FromText("Transfer 1 000 USD to account X."));
var resp1 = await client.SendMessageAsync(new SendMessageRequest { Message = msg1 });

if (resp1.PayloadCase != SendMessageResponseCase.Task)
{
    Console.WriteLine($"ERROR: expected Task response, got {resp1.PayloadCase}");
    await app.StopAsync();
    return;
}

var task1  = resp1.Task!;
var taskId = task1.Id;
Console.WriteLine($"step 1 — state: {task1.Status.State}, taskId: {taskId}");   // InputRequired
Console.WriteLine();

// ── step 2: resume — supply approval text with the same TaskId ──
var msg2 = new Message
{
    Role      = Role.User,
    MessageId = Guid.NewGuid().ToString("N"),
    TaskId    = taskId,                     // links this message to the interrupted task
};
msg2.Parts.Add(Part.FromText("Approved by alice@example.com."));
var resp2 = await client.SendMessageAsync(new SendMessageRequest { Message = msg2 });

switch (resp2.PayloadCase)
{
    case SendMessageResponseCase.Task:
        Console.WriteLine($"step 2 — state: {resp2.Task!.Status.State}");       // Completed
        break;
    case SendMessageResponseCase.Message:
        Console.WriteLine($"step 2 — reply: \"{resp2.Message!.Parts[0].Text}\"");
        break;
}

await app.StopAsync();

// ── helpers ──

static int FindFreePort()
{
    using var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    l.Start();
    var p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
    l.Stop();
    return p;
}

// Scripted provider:
//   Turn 1 — emits a tool call that the guardrail will intercept.
//   Turn 2 (resume) — emits the final answer; no tool call, so the guardrail does not run.
sealed class InterruptOnceProvider : ICompletionProvider
{
    private volatile bool _toolCalled;
    public string ProviderName => "interrupt-once";

    public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken ct = default)
    {
        if (!_toolCalled)
        {
            _toolCalled = true;
            var toolCall = new ToolCallRequest(
                ToolName:  "approve_action",
                Arguments: JsonDocument.Parse("""{"action":"transfer","amount":1000}""").RootElement.Clone(),
                CallId:    "call-1");
            return Task.FromResult(new CompletionResponse("", "scripted", ToolCalls: [toolCall]));
        }
        return Task.FromResult(new CompletionResponse(
            "Transfer approved and completed.", ModelId: "scripted"));
    }
}

// Tool: represents a high-value action requiring human approval before execution.
sealed class ApproveActionTool : ITool
{
    public string Name        => "approve_action";
    public string Description => "Execute a high-value action. Requires prior human approval.";
    public JsonElement ParametersSchema { get; } = JsonDocument.Parse(
        """{"type":"object","properties":{"action":{"type":"string"},"amount":{"type":"number"}}}""")
        .RootElement.Clone();

    public Task<string> InvokeAsync(JsonElement args, CancellationToken ct = default)
        => Task.FromResult("done.");
}

// Guardrail: interrupts before any approve_action invocation to request human sign-off.
sealed class ApprovalGuardrail : IToolGuardrail
{
    public ValueTask<GuardrailOutcome> BeforeInvokeAsync(
        ITool tool, JsonElement arguments, AgentContext ctx, CancellationToken ct = default)
    {
        var interrupt = new AgentInterrupt(
            InterruptId: Guid.NewGuid().ToString("N"),
            Reason:      $"human approval required to run '{tool.Name}'",
            Payload:     arguments);
        return ValueTask.FromResult(GuardrailOutcome.Interrupt(interrupt));
    }

    public ValueTask<GuardrailOutcome> AfterInvokeAsync(
        ITool tool, JsonElement arguments, string result, AgentContext ctx, CancellationToken ct = default)
        => ValueTask.FromResult(GuardrailOutcome.Pass);
}

sealed class SingletonRegistry(ITool tool) : IToolRegistry
{
    public IReadOnlyList<ITool> Tools     { get; } = [tool];
    public ITool? GetByName(string name) => Tools.FirstOrDefault(t => t.Name == name);
}
