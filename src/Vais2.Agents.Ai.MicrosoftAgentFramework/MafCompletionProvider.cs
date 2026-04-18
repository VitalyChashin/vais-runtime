// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MeaiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using MeaiChatRole = Microsoft.Extensions.AI.ChatRole;
using VaisChatRole = Vais2.Agents.ChatRole;

namespace Vais2.Agents.Ai.MicrosoftAgentFramework;

/// <summary>
/// Microsoft Agent Framework-backed <see cref="ICompletionProvider"/>.
/// </summary>
/// <remarks>
/// <para>
/// Uses MAF's <see cref="AIAgent"/> (constructed via the
/// <c>IChatClient.CreateAIAgent(...)</c> extension) rather than hitting
/// <see cref="IChatClient"/> directly. That means the MAF-specific code path is
/// exercised: agent options, tools (when added in a future milestone), and MAF's
/// <c>AgentRunResponse</c> usage shape.
/// </para>
/// <para>
/// History is authoritative in the Vais2.Agents core; we pass the full turn list
/// each call and intentionally supply no session / thread. That keeps the
/// neutral core as the single source of truth for conversation state — important
/// for Orleans-backed grain persistence in later milestones.
/// </para>
/// </remarks>
public sealed class MafCompletionProvider : ICompletionProvider, IStreamingCompletionProvider
{
    private readonly IChatClient _chatClient;
    private readonly string _agentName;
    private readonly string _modelId;

    /// <summary>
    /// Create a provider bound to a MEAI <see cref="IChatClient"/>.
    /// </summary>
    /// <param name="chatClient">The underlying chat client. Must not be null.</param>
    /// <param name="agentName">
    /// Name attached to the wrapped <see cref="AIAgent"/>. Visible in traces and some
    /// MAF orchestration primitives; has no semantic effect on single-agent runs.
    /// </param>
    /// <param name="modelId">
    /// Model identifier for telemetry. If null, the provider attempts to resolve it
    /// from the chat client's <see cref="ChatClientMetadata"/>.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="chatClient"/> is null.</exception>
    public MafCompletionProvider(
        IChatClient chatClient,
        string agentName = "vais2-agent",
        string? modelId = null)
    {
        ArgumentNullException.ThrowIfNull(chatClient);

        // Wrap the chat client once with FunctionInvokingChatClient. MAF's
        // ChatClientAgent requires it in the pipeline to auto-invoke tools;
        // without tools in a given turn the wrapper passes through harmlessly,
        // so we pay the wrapping cost once at construction and never again.
        _chatClient = chatClient.AsBuilder().UseFunctionInvocation().Build();
        _agentName = agentName;
        _modelId = modelId ?? ResolveModelId(chatClient);
    }

    /// <inheritdoc />
    public string ProviderName => "MicrosoftAgentFramework";

    /// <inheritdoc />
    public async Task<CompletionResponse> CompleteAsync(
        CompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Build a fresh MAF agent each call. Caching by (chatClient, instructions) is
        // a later optimisation — avoid it here to keep the adapter stateless and
        // obviously correct.
        var agent = _chatClient.CreateAIAgent(
            name: _agentName,
            instructions: request.SystemPrompt,
            description: "Vais2.Agents MAF-backed agent");

        var messages = request.History.Select(ToMeai).ToList();

        var chatOptions = new ChatOptions
        {
            MaxOutputTokens = request.MaxTokens ?? 1024,
            Temperature = request.Temperature ?? 0.2f,
        };

        if (request.Tools is { Count: > 0 } tools)
        {
            chatOptions.Tools = MafToolBinder.BuildTools(tools);
        }

        var options = new ChatClientAgentRunOptions { ChatOptions = chatOptions };

        var response = await agent
            .RunAsync(messages, thread: null, options: options, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var text = response.Text ?? string.Empty;

        int? promptTokens = null;
        int? completionTokens = null;
        if (response.Usage is { } usage)
        {
            promptTokens = (int?)usage.InputTokenCount;
            completionTokens = (int?)usage.OutputTokenCount;
        }

        return new CompletionResponse(text, _modelId, promptTokens, completionTokens);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<CompletionUpdate> StreamAsync(
        CompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var agent = _chatClient.CreateAIAgent(
            name: _agentName,
            instructions: request.SystemPrompt,
            description: "Vais2.Agents MAF-backed agent");

        var messages = request.History.Select(ToMeai).ToList();

        var chatOptions = new ChatOptions
        {
            MaxOutputTokens = request.MaxTokens ?? 1024,
            Temperature = request.Temperature ?? 0.2f,
        };

        if (request.Tools is { Count: > 0 } tools)
        {
            chatOptions.Tools = MafToolBinder.BuildTools(tools);
        }

        var options = new ChatClientAgentRunOptions { ChatOptions = chatOptions };

        // MAF's RunStreamingAsync emits AgentRunResponseUpdate items — each carries a Text fragment
        // plus (typically only on the last item) the Usage block. The wrapping FunctionInvokingChatClient
        // continues to auto-invoke tools on this path, so tool-calling streams work the same way
        // tool-calling on the non-streaming path does.
        await foreach (var update in agent
            .RunStreamingAsync(messages, thread: null, options: options, cancellationToken: cancellationToken)
            .ConfigureAwait(false))
        {
            var delta = update.Text ?? string.Empty;

            int? promptTokens = null;
            int? completionTokens = null;
            var usageContent = update.Contents.OfType<UsageContent>().FirstOrDefault();
            if (usageContent?.Details is { } usage)
            {
                promptTokens = (int?)usage.InputTokenCount;
                completionTokens = (int?)usage.OutputTokenCount;
            }

            yield return new CompletionUpdate(delta, _modelId, promptTokens, completionTokens);
        }
    }

    private static MeaiChatMessage ToMeai(ChatTurn turn) => turn.Role switch
    {
        VaisChatRole.System => new MeaiChatMessage(MeaiChatRole.System, turn.Text),
        VaisChatRole.User => new MeaiChatMessage(MeaiChatRole.User, turn.Text),
        VaisChatRole.Assistant => new MeaiChatMessage(MeaiChatRole.Assistant, turn.Text),
        _ => throw new ArgumentOutOfRangeException(nameof(turn), turn.Role, "Unsupported chat role."),
    };

    private static string ResolveModelId(IChatClient client)
    {
        var meta = client.GetService(typeof(ChatClientMetadata)) as ChatClientMetadata;
        return meta?.DefaultModelId ?? "unknown";
    }
}
