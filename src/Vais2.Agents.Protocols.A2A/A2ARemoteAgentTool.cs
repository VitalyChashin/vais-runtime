// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text;
using System.Text.Json;
using A2A;

namespace Vais2.Agents.Protocols.A2A;

/// <summary>
/// <see cref="ITool"/> adapter that exposes a remote A2A agent as a local
/// tool, so a local agent can delegate a sub-task to a peer agent over the
/// Agent-to-Agent protocol.
/// </summary>
/// <remarks>
/// <para>
/// <b>Input schema.</b> The A2A <c>AgentCard</c> describes skills but not a
/// single "input shape" — a tool-call model just needs one string to send.
/// This adapter declares a fixed
/// <c>{"type":"object","properties":{"message":{"type":"string"}},"required":["message"]}</c>
/// schema; callers wanting structured sub-agent I/O should wire it manually.
/// </para>
/// <para>
/// <b>Response extraction.</b> A2A's <c>SendMessageAsync</c> returns a
/// polymorphic <see cref="A2AResponse"/>. We handle two shapes:
/// <see cref="AgentMessage"/> (concatenate <see cref="TextPart.Text"/> blocks)
/// and <see cref="AgentTask"/> (concatenate text blocks across all artifacts).
/// Anything else (streaming events, empty responses) raises
/// <see cref="A2AAgentInvocationException"/>.
/// </para>
/// <para>
/// <b>Name sanitisation.</b> <see cref="ITool.Name"/> must match
/// <c>[A-Za-z0-9_-]+</c>. <see cref="AgentCard.Name"/> is free-form, so we
/// replace any non-matching char with <c>_</c>, collapse runs, and trim.
/// </para>
/// </remarks>
public sealed class A2ARemoteAgentTool : ITool
{
    private static readonly JsonElement s_parametersSchema = JsonDocument.Parse(
        """{"type":"object","properties":{"message":{"type":"string","description":"Text prompt to send to the remote agent."}},"required":["message"]}""")
        .RootElement.Clone();

    private readonly IA2AClient _client;
    private readonly string _agentName;

    /// <summary>The original (unsanitised) <see cref="AgentCard.Name"/> of the remote agent.</summary>
    public string AgentName => _agentName;

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public string Description { get; }

    /// <inheritdoc />
    public JsonElement ParametersSchema => s_parametersSchema;

    /// <summary>
    /// Wrap an already-constructed <see cref="IA2AClient"/> + <see cref="AgentCard"/>.
    /// Prefer <see cref="CreateAsync"/> when you only have the agent's URL — it runs
    /// the card-resolver for you.
    /// </summary>
    /// <param name="client">The A2A client. Must be pre-configured against the same URL as <paramref name="card"/>.</param>
    /// <param name="card">The resolved agent card — provides <see cref="ITool.Name"/> and <see cref="ITool.Description"/>.</param>
    public A2ARemoteAgentTool(IA2AClient client, AgentCard card)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(card);

        _client = client;
        _agentName = card.Name ?? string.Empty;
        Name = SanitiseToolName(_agentName);
        Description = card.Description ?? string.Empty;
    }

    /// <summary>
    /// Discover the remote agent via <see cref="A2ACardResolver"/>, build an
    /// <see cref="A2AClient"/>, and wrap both as an <see cref="ITool"/>.
    /// </summary>
    /// <param name="agentUrl">The base URL of the remote agent's hosting service.</param>
    /// <param name="httpClient">Optional shared <see cref="HttpClient"/>. Null lets the SDK manage one.</param>
    /// <param name="cancellationToken">Cancels the card-resolution HTTP call.</param>
    public static async Task<A2ARemoteAgentTool> CreateAsync(
        Uri agentUrl,
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agentUrl);

        var resolver = new A2ACardResolver(agentUrl, httpClient!);
        var card = await resolver.GetAgentCardAsync(cancellationToken).ConfigureAwait(false);

        var client = new A2AClient(agentUrl, httpClient!);
        return new A2ARemoteAgentTool(client, card);
    }

    /// <inheritdoc />
    public async Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var messageText = ExtractMessage(arguments);

        var agentMessage = new AgentMessage
        {
            Role = MessageRole.User,
            MessageId = Guid.NewGuid().ToString("N"),
            Parts = [new TextPart { Text = messageText }],
        };

        var response = await _client.SendMessageAsync(
            new MessageSendParams { Message = agentMessage },
            cancellationToken).ConfigureAwait(false);

        return ExtractText(response, _agentName);
    }

    private static string ExtractMessage(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        if (arguments.TryGetProperty("message", out var messageElement) &&
            messageElement.ValueKind == JsonValueKind.String)
        {
            return messageElement.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static string ExtractText(A2AResponse response, string agentName)
    {
        switch (response)
        {
            case AgentMessage message:
            {
                var text = ConcatenateTextParts(message.Parts);
                if (string.IsNullOrEmpty(text))
                {
                    throw new A2AAgentInvocationException(agentName, "response message contained no text parts.");
                }
                return text;
            }

            case AgentTask task:
            {
                var sb = new StringBuilder();
                if (task.Artifacts is not null)
                {
                    foreach (var artifact in task.Artifacts)
                    {
                        var artifactText = ConcatenateTextParts(artifact.Parts);
                        if (artifactText.Length == 0)
                        {
                            continue;
                        }
                        if (sb.Length > 0)
                        {
                            sb.Append('\n');
                        }
                        sb.Append(artifactText);
                    }
                }

                if (sb.Length == 0)
                {
                    throw new A2AAgentInvocationException(agentName, $"task '{task.Id}' produced no text artifacts.");
                }
                return sb.ToString();
            }

            default:
                throw new A2AAgentInvocationException(agentName, $"unsupported response kind: {response.GetType().Name}.");
        }
    }

    private static string ConcatenateTextParts(IReadOnlyList<Part>? parts)
    {
        if (parts is null || parts.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            if (part is TextPart tp && !string.IsNullOrEmpty(tp.Text))
            {
                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }
                sb.Append(tp.Text);
            }
        }
        return sb.ToString();
    }

    internal static string SanitiseToolName(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            throw new ArgumentException("Agent card did not provide a Name; cannot derive a tool name.", nameof(raw));
        }

        var sb = new StringBuilder(raw.Length);
        var lastWasUnderscore = false;
        foreach (var ch in raw)
        {
            if ((ch >= 'A' && ch <= 'Z') ||
                (ch >= 'a' && ch <= 'z') ||
                (ch >= '0' && ch <= '9') ||
                ch == '_' || ch == '-')
            {
                sb.Append(ch);
                lastWasUnderscore = false;
            }
            else
            {
                if (!lastWasUnderscore)
                {
                    sb.Append('_');
                    lastWasUnderscore = true;
                }
            }
        }

        var sanitised = sb.ToString().Trim('_', '-');
        if (sanitised.Length == 0)
        {
            throw new ArgumentException($"Agent card Name '{raw}' sanitised to empty; cannot derive a tool name.", nameof(raw));
        }
        return sanitised;
    }
}
