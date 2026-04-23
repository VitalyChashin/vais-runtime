// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net.Http.Headers;
using System.Text;
using A2A;

namespace Vais.Agents.Protocols.A2A;

/// <summary>
/// <see cref="IA2AGraphNodeInvoker"/> implementation that sends text
/// messages to remote A2A agents and extracts text responses.
/// Reuses the same response-extraction pattern as <see cref="A2ARemoteAgentTool"/>.
/// </summary>
public sealed class A2AGraphNodeInvoker : IA2AGraphNodeInvoker
{
    /// <inheritdoc />
    public async ValueTask<string> InvokeAsync(
        string a2aUrl,
        string message,
        string? bearerToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(a2aUrl);

        using var http = new HttpClient();
        if (!string.IsNullOrEmpty(bearerToken))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        var agentUri = new Uri(a2aUrl.TrimEnd('/'));
        var client = new A2AClient(agentUri, http);

        var msg = new Message
        {
            Role = Role.User,
            MessageId = Guid.NewGuid().ToString("N"),
        };
        msg.Parts.Add(Part.FromText(message));

        var response = await client.SendMessageAsync(
            new SendMessageRequest { Message = msg },
            cancellationToken).ConfigureAwait(false);

        return ExtractText(response, a2aUrl);
    }

    internal static string ExtractText(SendMessageResponse response, string a2aUrl)
    {
        switch (response.PayloadCase)
        {
            case SendMessageResponseCase.Message:
            {
                var message = response.Message;
                var text = message is null ? string.Empty : ConcatenateTextParts(message.Parts);
                return string.IsNullOrEmpty(text)
                    ? throw new A2AAgentInvocationException(a2aUrl, "response message contained no text parts.")
                    : text;
            }

            case SendMessageResponseCase.Task:
            {
                var task = response.Task;
                var sb = new StringBuilder();
                if (task?.Artifacts is not null)
                {
                    foreach (var artifact in task.Artifacts)
                    {
                        var artifactText = ConcatenateTextParts(artifact.Parts);
                        if (artifactText.Length == 0) continue;
                        if (sb.Length > 0) sb.Append('\n');
                        sb.Append(artifactText);
                    }
                }

                return sb.Length == 0
                    ? throw new A2AAgentInvocationException(a2aUrl, $"task '{task?.Id}' produced no text artifacts.")
                    : sb.ToString();
            }

            default:
                throw new A2AAgentInvocationException(a2aUrl, $"unsupported response payload: {response.PayloadCase}.");
        }
    }

    private static string ConcatenateTextParts(IList<Part>? parts)
    {
        if (parts is null || parts.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            if (part.ContentCase != PartContentCase.Text) continue;
            var text = part.Text;
            if (string.IsNullOrEmpty(text)) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(text);
        }
        return sb.ToString();
    }
}
