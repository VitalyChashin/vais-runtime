// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vais.Agents.Core;


namespace Vais.Agents.Runtime.Plugins.Python;

/// <summary>
/// <see cref="IAiAgent"/> backed by a Python subprocess that speaks the
/// <c>vais/agent.*</c> JSON-RPC extension over the MCP stdio channel (v0.24).
/// One instance is created per grain activation; the subprocess's opaque state
/// blob is held in memory and round-tripped on every <see cref="AskAsync"/> call.
/// </summary>
/// <remarks>
/// <para>
/// The shim owns no LLM calls itself — it delegates the entire agent loop to
/// the Python process, which drives reasoning, tool calls, and state updates
/// independently. The .NET side provides Orleans durability, registry activation,
/// policy/audit plumbing, and the <c>vais agent apply</c> CLI surface.
/// </para>
/// <para>
/// Thread safety: not thread-safe. Callers (Orleans grain activations) provide
/// the single-writer guarantee.
/// </para>
/// </remarks>
internal sealed class PythonAgentShim : IAiAgent, IStreamingAiAgent, IOpaqueStateCarrier
{
    private static readonly ActivitySource _activitySource =
        new("Vais.Agents.Runtime.Plugins.Python", "1.0.0");

    private readonly IPythonAgentChannel _supervisor;
    private readonly int _maxStateSizeBytes;
    private readonly ILogger _logger;
    private string? _opaqueState;

    internal PythonAgentShim(
        IPythonAgentChannel supervisor,
        InMemoryAgentSession session,
        int maxStateSizeBytes,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(supervisor);
        ArgumentNullException.ThrowIfNull(session);
        _supervisor = supervisor;
        Session = session;
        _maxStateSizeBytes = maxStateSizeBytes;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    string? IOpaqueStateCarrier.OpaqueState
    {
        get => _opaqueState;
        set => _opaqueState = value;
    }

    /// <inheritdoc />
    public string? SystemPrompt { get; set; }

    /// <inheritdoc />
    public IAgentSession Session { get; }

    /// <inheritdoc />
    public IReadOnlyList<ChatTurn> History => Session.History;

    /// <inheritdoc />
    public async Task<string> AskAsync(string userMessage, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);

        // Append user turn before the call — mirrors StatefulAiAgent. Grain re-activation
        // clears in-memory state, so an orphaned user turn (on failure) is harmless.
        await Session.AppendAsync(new ChatTurn(AgentChatRole.User, userMessage), cancellationToken)
            .ConfigureAwait(false);

        var request = new AgentInvokeRequest(
            AgentId: Session.AgentId,
            SessionId: Session.SessionId,
            UserMessage: userMessage,
            State: _opaqueState,
            TimeoutSeconds: _supervisor.Descriptor.InvokeTimeoutSeconds,
            Context: null);

        var graphRunId = Activity.Current?.GetTagItem("graph.run_id") as string;
        using var activity = _activitySource.StartActivity("python.agent.ask", ActivityKind.Internal);
        activity?.SetTag("vais.agent.name", Session.AgentId);
        activity?.SetTag("gen_ai.prompt",   userMessage);
        if (graphRunId != null) activity?.SetTag("graph.run_id", graphRunId);

        var response = await _supervisor.InvokeAgentAsync(request, cancellationToken).ConfigureAwait(false);

        activity?.SetTag("gen_ai.completion", response.AssistantMessage);

        // Guard against oversized state blobs before accepting the turn.
        if (response.NewState is { } ns &&
            _maxStateSizeBytes > 0 &&
            Encoding.UTF8.GetByteCount(ns) > _maxStateSizeBytes)
        {
            _logger.LogWarning(
                "[{Urn}] Python agent '{AgentId}' returned state of {Size} bytes " +
                "(limit {Limit}) — turn rejected, previous state preserved.",
                PythonPluginUrns.AgentStateTooLarge,
                Session.AgentId,
                Encoding.UTF8.GetByteCount(ns),
                _maxStateSizeBytes);
            throw new InvalidOperationException(
                $"[{PythonPluginUrns.AgentStateTooLarge}] Python agent state exceeded " +
                $"{_maxStateSizeBytes} bytes.");
        }

        _opaqueState = response.NewState;
        await Session.AppendAsync(
            new ChatTurn(AgentChatRole.Assistant, response.AssistantMessage), cancellationToken)
            .ConfigureAwait(false);

        return response.AssistantMessage;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AgentEvent> StreamAsync(
        string userMessage,
        AgentContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);

        await Session.AppendAsync(new ChatTurn(AgentChatRole.User, userMessage), cancellationToken)
            .ConfigureAwait(false);

        var start = DateTimeOffset.UtcNow;
        yield return new TurnStarted(start, context, userMessage);

        var request = new AgentInvokeRequest(
            AgentId: Session.AgentId,
            SessionId: Session.SessionId,
            UserMessage: userMessage,
            State: _opaqueState,
            TimeoutSeconds: _supervisor.Descriptor.InvokeTimeoutSeconds,
            Context: null);

        AgentInvokeResponse? finalResponse = null;
        Exception? streamError = null;
        bool wasCancelled = false;

        // Manual enumeration lets us catch exceptions from MoveNextAsync while still
        // yielding inside the outer try/finally (yield in try+catch is disallowed by C#).
        var enumerator = _supervisor.StreamAgentAsync(request, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    wasCancelled = true;
                    break;
                }
                catch (Exception ex)
                {
                    streamError = ex;
                    break;
                }

                if (!hasNext) break;

                var frame = enumerator.Current;
                if (frame.TextDelta is { } text)
                    yield return new CompletionDelta(DateTimeOffset.UtcNow, context, text);
                else if (frame.FinalResponse is { } resp)
                    finalResponse = resp;
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }

        if (wasCancelled)
            yield break;

        if (streamError is not null)
        {
            yield return new TurnFailed(DateTimeOffset.UtcNow, context,
                streamError.GetType().Name, streamError.Message, DateTimeOffset.UtcNow - start);
            yield break;
        }

        if (finalResponse is null)
        {
            yield return new TurnFailed(DateTimeOffset.UtcNow, context,
                "StreamError", "No final response received from Python agent.",
                DateTimeOffset.UtcNow - start);
            yield break;
        }

        if (finalResponse.NewState is { } ns &&
            _maxStateSizeBytes > 0 &&
            Encoding.UTF8.GetByteCount(ns) > _maxStateSizeBytes)
        {
            _logger.LogWarning(
                "[{Urn}] Python agent '{AgentId}' returned state of {Size} bytes " +
                "(limit {Limit}) — stream turn rejected, previous state preserved.",
                PythonPluginUrns.AgentStateTooLarge,
                Session.AgentId,
                Encoding.UTF8.GetByteCount(ns),
                _maxStateSizeBytes);
            yield return new TurnFailed(DateTimeOffset.UtcNow, context,
                "AgentStateTooLarge",
                $"[{PythonPluginUrns.AgentStateTooLarge}] Python agent state exceeded {_maxStateSizeBytes} bytes.",
                DateTimeOffset.UtcNow - start);
            yield break;
        }

        _opaqueState = finalResponse.NewState;
        await Session.AppendAsync(
            new ChatTurn(AgentChatRole.Assistant, finalResponse.AssistantMessage), cancellationToken)
            .ConfigureAwait(false);

        yield return new TurnCompleted(
            DateTimeOffset.UtcNow,
            context,
            finalResponse.AssistantMessage,
            ModelId: finalResponse.Usage?.FirstOrDefault()?.Model,
            PromptTokens: finalResponse.Usage?.Sum(u => u.InputTokens),
            CompletionTokens: finalResponse.Usage?.Sum(u => u.OutputTokens),
            Duration: DateTimeOffset.UtcNow - start);
    }

    /// <inheritdoc />
    public void Reset()
    {
        _opaqueState = null;
        // InMemoryAgentSession.ResetAsync completes synchronously.
        var vt = Session.ResetAsync();
        if (!vt.IsCompleted)
            vt.AsTask().GetAwaiter().GetResult();
    }
}
