// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais2.Agents.Hosting.Orleans;

/// <summary>
/// Client-side <see cref="IAiAgent"/> adapter over an <see cref="IAiAgentGrain"/>.
/// Translates between the synchronous property surface of <see cref="IAiAgent"/> and the
/// async, grain-local authoritative state on the silo side.
/// </summary>
/// <remarks>
/// <para>
/// <b>Caching.</b> <see cref="History"/> and <see cref="SystemPrompt"/> are cached on the
/// client; the cache is refreshed after every <see cref="AskAsync"/>. Between turns the
/// properties return the last observed snapshot — stale if another client mutates the
/// grain concurrently. Call <see cref="AskAsync"/> or recreate the proxy via
/// <see cref="IAgentRuntime.GetOrCreate"/> if fresh state is required.
/// </para>
/// <para>
/// <b>Threading.</b> The proxy is designed for use from non-grain contexts (host services,
/// controllers, background workers). The <see cref="SystemPrompt"/> setter and
/// <see cref="Reset"/> block synchronously on the grain call; invoking them from inside
/// another grain's turn would deadlock the single-threaded grain scheduler. In those
/// contexts use the underlying <see cref="IAiAgentGrain"/> methods directly.
/// </para>
/// </remarks>
internal sealed class OrleansAiAgentProxy : IAiAgent
{
    private readonly IAiAgentGrain _grain;
    private IReadOnlyList<ChatTurn> _historyCache = Array.Empty<ChatTurn>();
    private string? _systemPromptCache;
    private bool _cacheInitialised;

    public OrleansAiAgentProxy(IAiAgentGrain grain)
    {
        ArgumentNullException.ThrowIfNull(grain);
        _grain = grain;
    }

    public string? SystemPrompt
    {
        get
        {
            EnsureCache();
            return _systemPromptCache;
        }
        set
        {
            _grain.SetSystemPromptAsync(value).ConfigureAwait(false).GetAwaiter().GetResult();
            _systemPromptCache = value;
            _cacheInitialised = true;
        }
    }

    public IReadOnlyList<ChatTurn> History
    {
        get
        {
            EnsureCache();
            return _historyCache;
        }
    }

    public async Task<string> AskAsync(string userMessage, CancellationToken cancellationToken = default)
    {
        var reply = await _grain.AskAsync(userMessage).ConfigureAwait(false);
        _historyCache = await _grain.GetHistoryAsync().ConfigureAwait(false);
        _systemPromptCache = await _grain.GetSystemPromptAsync().ConfigureAwait(false);
        _cacheInitialised = true;
        return reply;
    }

    public void Reset()
    {
        _grain.ResetAsync().ConfigureAwait(false).GetAwaiter().GetResult();
        _historyCache = Array.Empty<ChatTurn>();
    }

    private void EnsureCache()
    {
        if (_cacheInitialised)
        {
            return;
        }

        _historyCache = _grain.GetHistoryAsync().ConfigureAwait(false).GetAwaiter().GetResult();
        _systemPromptCache = _grain.GetSystemPromptAsync().ConfigureAwait(false).GetAwaiter().GetResult();
        _cacheInitialised = true;
    }
}
