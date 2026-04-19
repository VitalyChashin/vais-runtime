// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using A2A;
using Vais.Agents;
using Vais.Agents.Core;
using Vais.Agents.Protocols.A2A;

// -----------------------------------------------------------------------------
// A2ARemoteAgentExample — shows A2ARemoteAgentTool wrapping a stubbed IA2AClient
// end-to-end. A real consumer uses A2ARemoteAgentTool.CreateAsync(uri) to
// resolve the remote AgentCard + build the client against a live A2A endpoint;
// here we inject a stub so the invocation path runs offline.
// -----------------------------------------------------------------------------

var card = new AgentCard { Name = "weather-bot", Description = "Answers weather questions." };
var stubClient = new StubA2AClient(cannedReply: "Paris is mild and sunny, 72°F.");
var remote = new A2ARemoteAgentTool(stubClient, card);

Console.WriteLine($"wrapped tool name: {remote.Name}");
Console.WriteLine($"description:       {remote.Description}");
Console.WriteLine($"schema:            {remote.ParametersSchema.GetRawText()}");
Console.WriteLine();

// Use as any other ITool — feed into the agent's registry.
var registry = new SingletonRegistry(remote);
var driverProvider = new ScriptedToolCallingProvider();
var agent = new StatefulAiAgent(driverProvider, new StatefulAgentOptions { ToolRegistry = registry });

Console.WriteLine("=== driver agent delegates to the remote A2A peer ===");
Console.WriteLine(await agent.AskAsync("What's the weather in Paris?"));

// ---- Stub IA2AClient ----
sealed class StubA2AClient(string cannedReply) : IA2AClient
{
    public Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, CancellationToken ct = default)
    {
        var msg = new Message { Role = Role.Agent, MessageId = Guid.NewGuid().ToString("N") };
        msg.Parts.Add(Part.FromText(cannedReply));
        return Task.FromResult(new SendMessageResponse { Message = msg });
    }

    // Every other IA2AClient method is unused in this sample — stub with NotSupportedException.
    public IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest r, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<AgentTask> GetTaskAsync(GetTaskRequest r, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<ListTasksResponse> ListTasksAsync(ListTasksRequest r, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<AgentTask> CancelTaskAsync(CancelTaskRequest r, CancellationToken ct = default) => throw new NotSupportedException();
    public IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest r, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(CreateTaskPushNotificationConfigRequest r, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest r, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<ListTaskPushNotificationConfigResponse> ListTaskPushNotificationConfigAsync(ListTaskPushNotificationConfigRequest r, CancellationToken ct = default) => throw new NotSupportedException();
    public Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest r, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest r, CancellationToken ct = default) => throw new NotSupportedException();
}

// ---- Scripted driver provider ----
sealed class ScriptedToolCallingProvider : ICompletionProvider
{
    private bool _calledTool;
    public string ProviderName => "driver";
    public Task<CompletionResponse> CompleteAsync(CompletionRequest req, CancellationToken ct = default)
    {
        if (!_calledTool)
        {
            _calledTool = true;
            var call = new ToolCallRequest(
                ToolName: "weather-bot",
                Arguments: System.Text.Json.JsonDocument.Parse("""{"message":"forecast Paris"}""").RootElement.Clone(),
                CallId: "call-1");
            return Task.FromResult(new CompletionResponse("", ModelId: "fake-model", ToolCalls: new[] { call }));
        }
        var toolResult = req.History.LastOrDefault(t => t.Role == AgentChatRole.Tool)?.Text ?? "(no result)";
        return Task.FromResult(new CompletionResponse(
            $"The peer reported: {toolResult}",
            ModelId: "fake-model"));
    }
}

sealed class SingletonRegistry(ITool tool) : IToolRegistry
{
    public IReadOnlyList<ITool> Tools { get; } = new[] { tool };
    public ITool? GetByName(string name) => Tools.FirstOrDefault(t => t.Name == name);
}
