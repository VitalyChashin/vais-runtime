// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

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
internal sealed class PythonAgentShim : IAiAgent, IOpaqueStateCarrier
{
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

        var response = await _supervisor.InvokeAgentAsync(request, cancellationToken).ConfigureAwait(false);

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
    public void Reset()
    {
        _opaqueState = null;
        // InMemoryAgentSession.ResetAsync completes synchronously.
        var vt = Session.ResetAsync();
        if (!vt.IsCompleted)
            vt.AsTask().GetAwaiter().GetResult();
    }
}
