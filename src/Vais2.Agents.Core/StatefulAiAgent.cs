// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Vais2.Agents.Core;

/// <summary>
/// Default, in-process <see cref="IAiAgent"/> implementation. Owns its own chat
/// history and delegates each turn to an injected <see cref="ICompletionProvider"/>.
/// </summary>
/// <remarks>
/// <para>
/// Stack-neutral: swapping SK for MAF is purely a DI change — this class does not
/// know which backend answered.
/// </para>
/// <para>
/// Not thread-safe. Concurrent calls into <see cref="AskAsync"/> on the same instance
/// would race on the history list. Agents are typically addressed by a stable
/// identifier (e.g. a grain key) elsewhere in the library; serialise calls per
/// instance at that layer.
/// </para>
/// </remarks>
public sealed class StatefulAiAgent : IAiAgent
{
    private readonly ICompletionProvider _provider;
    private readonly ILogger<StatefulAiAgent> _logger;
    private readonly List<ChatTurn> _history = new();

    /// <summary>
    /// Create a new agent bound to a completion provider.
    /// </summary>
    /// <param name="provider">The provider that executes each completion turn.</param>
    /// <param name="systemPrompt">Optional system instruction prepended to every turn.</param>
    /// <param name="logger">Optional logger. A null-logger is used if none is supplied.</param>
    /// <exception cref="ArgumentNullException"><paramref name="provider"/> is null.</exception>
    public StatefulAiAgent(
        ICompletionProvider provider,
        string? systemPrompt = null,
        ILogger<StatefulAiAgent>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
        _logger = logger ?? NullLogger<StatefulAiAgent>.Instance;
        SystemPrompt = systemPrompt;
    }

    /// <inheritdoc />
    public string? SystemPrompt { get; set; }

    /// <inheritdoc />
    public IReadOnlyList<ChatTurn> History => _history;

    /// <inheritdoc />
    public async Task<string> AskAsync(string userMessage, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
        {
            throw new ArgumentException("User message must be non-empty.", nameof(userMessage));
        }

        _history.Add(new ChatTurn(ChatRole.User, userMessage));

        _logger.LogDebug(
            "Dispatching completion via {Provider}, history={TurnCount} turns",
            _provider.ProviderName,
            _history.Count);

        // Snapshot: the provider must see a stable view of the history. The
        // in-process list keeps mutating across turns; handing out the live
        // reference would race with the next call or allow an adapter to mutate
        // our state.
        var snapshot = _history.ToArray();
        var response = await _provider.CompleteAsync(
            new CompletionRequest(snapshot, SystemPrompt),
            cancellationToken).ConfigureAwait(false);

        _history.Add(new ChatTurn(ChatRole.Assistant, response.Text));

        _logger.LogDebug(
            "Turn complete via {Provider}, usage={Total} tokens",
            _provider.ProviderName,
            response.TotalTokens?.ToString() ?? "unknown");

        return response.Text;
    }

    /// <inheritdoc />
    public void Reset() => _history.Clear();
}
