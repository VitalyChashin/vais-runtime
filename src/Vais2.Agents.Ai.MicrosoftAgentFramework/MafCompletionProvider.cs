// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MeaiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using MeaiChatRole = Microsoft.Extensions.AI.ChatRole;
using MeaiFunctionCallContent = Microsoft.Extensions.AI.FunctionCallContent;
using MeaiFunctionResultContent = Microsoft.Extensions.AI.FunctionResultContent;
using VaisChatRole = Vais2.Agents.AgentChatRole;

namespace Vais2.Agents.Ai.MicrosoftAgentFramework;

/// <summary>
/// Microsoft Agent Framework-backed <see cref="ICompletionProvider"/>.
/// </summary>
/// <remarks>
/// <para>
/// Uses MAF's <see cref="ChatClientAgent"/> (constructed directly around the
/// injected <see cref="IChatClient"/>) rather than hitting
/// <see cref="IChatClient"/> directly. That means the MAF-specific code path is
/// exercised: agent options, tools, and MAF's <c>AgentRunResponse</c> usage shape.
/// </para>
/// <para>
/// History is authoritative in the Vais2.Agents core; we pass the full turn list
/// each call and intentionally supply no <see cref="AgentSession"/>. That keeps
/// the neutral core as the single source of truth for conversation state —
/// important for Orleans-backed grain persistence.
/// </para>
/// <para>
/// <b>Tool-call behavior (v0.4+):</b> the agent is constructed with
/// <c>UseProvidedChatClientAsIs = true</c> so MAF does NOT add its default
/// <c>FunctionInvokingChatClient</c> wrapping. Tool calls surface as
/// <see cref="MeaiFunctionCallContent"/> items on the response message and get
/// mapped to <see cref="CompletionResponse.ToolCalls"/> for <c>StatefulAiAgent</c>'s
/// loop to dispatch. Tool-using streaming is not supported through
/// <c>StatefulAiAgent.StreamAsync</c> in v0.4 — if the model requests tools on a
/// streaming call the deltas will be empty.
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

        // v0.4: we no longer wrap with UseFunctionInvocation(). Instead, we pass
        // UseProvidedChatClientAsIs = true on the ChatClientAgentOptions so MAF
        // does not inject its default FunctionInvokingChatClient middleware. Tool
        // calls surface as MEAI FunctionCallContent items; StatefulAiAgent's
        // outer loop dispatches them via IToolCallDispatcher.
        _chatClient = chatClient;
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
        var agent = BuildAgent();

        var messages = BuildMessages(request);

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
            .RunAsync(messages, session: null, options: options, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var text = response.Text ?? string.Empty;

        int? promptTokens = null;
        int? completionTokens = null;
        if (response.Usage is { } usage)
        {
            promptTokens = (int?)usage.InputTokenCount;
            completionTokens = (int?)usage.OutputTokenCount;
        }

        var toolCalls = ExtractToolCalls(response.Messages);

        return new CompletionResponse(text, _modelId, promptTokens, completionTokens, toolCalls);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<CompletionUpdate> StreamAsync(
        CompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var agent = BuildAgent();

        var messages = BuildMessages(request);

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
        // plus (typically only on the last item) the Usage block. With UseProvidedChatClientAsIs we
        // don't auto-invoke tools here either; tool-using streaming is undefined in v0.4.
        await foreach (var update in agent
            .RunStreamingAsync(messages, session: null, options: options, cancellationToken: cancellationToken)
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

    private ChatClientAgent BuildAgent() =>
        new(
            _chatClient,
            new ChatClientAgentOptions
            {
                Name = _agentName,
                Description = "Vais2.Agents MAF-backed agent",
                UseProvidedChatClientAsIs = true,
            });

    // The options-shaped ChatClientAgent ctor doesn't carry `instructions`, so we
    // front-load the system prompt as a System-role message. MAF will pass it along
    // with the rest of the history to the underlying IChatClient on every call.
    private static List<MeaiChatMessage> BuildMessages(CompletionRequest request)
    {
        var messages = new List<MeaiChatMessage>();
        if (!string.IsNullOrEmpty(request.SystemPrompt))
        {
            messages.Add(new MeaiChatMessage(MeaiChatRole.System, request.SystemPrompt));
        }
        foreach (var turn in request.History)
        {
            messages.Add(ToMeai(turn));
        }
        return messages;
    }

    private static MeaiChatMessage ToMeai(ChatTurn turn)
    {
        switch (turn.Role)
        {
            case VaisChatRole.System:
                return new MeaiChatMessage(MeaiChatRole.System, turn.Text);
            case VaisChatRole.User:
                return new MeaiChatMessage(MeaiChatRole.User, turn.Text);
            case VaisChatRole.Assistant:
                if (turn.ToolCalls is { Count: > 0 } calls)
                {
                    var contents = new List<AIContent>();
                    if (!string.IsNullOrEmpty(turn.Text))
                    {
                        contents.Add(new TextContent(turn.Text));
                    }
                    foreach (var call in calls)
                    {
                        contents.Add(new MeaiFunctionCallContent(
                            callId: call.CallId,
                            name: call.ToolName,
                            arguments: JsonElementToDictionary(call.Arguments)));
                    }
                    return new MeaiChatMessage(MeaiChatRole.Assistant, contents);
                }
                return new MeaiChatMessage(MeaiChatRole.Assistant, turn.Text);
            case VaisChatRole.Tool:
                return new MeaiChatMessage(
                    MeaiChatRole.Tool,
                    new List<AIContent>
                    {
                        new MeaiFunctionResultContent(
                            callId: turn.ToolCallId ?? string.Empty,
                            result: turn.Text),
                    });
            default:
                throw new ArgumentOutOfRangeException(nameof(turn), turn.Role, "Unsupported chat role.");
        }
    }

    private static IReadOnlyList<ToolCallRequest>? ExtractToolCalls(IEnumerable<MeaiChatMessage> messages)
    {
        List<ToolCallRequest>? result = null;
        foreach (var message in messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is MeaiFunctionCallContent call)
                {
                    result ??= new List<ToolCallRequest>();
                    result.Add(new ToolCallRequest(
                        ToolName: call.Name,
                        Arguments: SerializeArguments(call.Arguments),
                        CallId: call.CallId));
                }
            }
        }
        return result;
    }

    private static JsonElement SerializeArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return JsonDocument.Parse("{}").RootElement;
        }
        return JsonSerializer.SerializeToElement(arguments);
    }

    private static Dictionary<string, object?>? JsonElementToDictionary(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var result = new Dictionary<string, object?>();
        foreach (var prop in arguments.EnumerateObject())
        {
            result[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.Number => prop.Value.TryGetInt64(out var l) ? (object?)l : prop.Value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => prop.Value.ToString(),
            };
        }
        return result;
    }

    private static string ResolveModelId(IChatClient client)
    {
        var meta = client.GetService(typeof(ChatClientMetadata)) as ChatClientMetadata;
        return meta?.DefaultModelId ?? "unknown";
    }
}
