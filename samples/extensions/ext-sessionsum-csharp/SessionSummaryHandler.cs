// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging;

[assembly: Vais.Agents.VaisExtension(
    TargetApiVersion = "0.30",
    Handlers = new[] { typeof(Vais.Agents.Samples.Extensions.SessionSummary.SessionSummarizer) })]

namespace Vais.Agents.Samples.Extensions.SessionSummary;

/// <summary>
/// Sample <see cref="SessionLifecycleHook"/> extension: logs session open/close and, on close, emits a
/// trivial "summary" derived from the conversation history. Demonstrates <c>host: csharp</c>
/// extension-authored session lifecycle handling — the same hook a co-tenant container agent gets via
/// <c>host: container</c> (see ext-sessionsum-python, mirrored shape).
/// </summary>
/// <remarks>
/// A real handler would persist a model-generated summary to a memory store. This sample just logs the
/// turn count + first user message. Close is best-effort (P1): a hard crash skips it, and idle grain
/// cycles produce extra open/close pairs.
/// </remarks>
public sealed class SessionSummarizer : SessionLifecycleHook
{
    private readonly ILogger<SessionSummarizer> _log;

    public SessionSummarizer(ILogger<SessionSummarizer> log) => _log = log;

    public override Task OnSessionAsync(SessionLifecycleContext context, CancellationToken cancellationToken = default)
    {
        if (string.Equals(context.Phase, SessionPhase.Opened, StringComparison.Ordinal))
        {
            _log.LogInformation("[ext-sessionsum] session opened agent={Agent} session={Session}",
                context.AgentId, context.SessionId);
            return Task.CompletedTask;
        }

        // closing — summarize from the history (best-effort).
        var firstUser = context.History?.FirstOrDefault(t => string.Equals(t.Role, "user", StringComparison.Ordinal))?.Text;
        _log.LogInformation(
            "[ext-sessionsum] session closing agent={Agent} session={Session} turns={Turns} summary=\"{Summary}\"",
            context.AgentId, context.SessionId, context.TurnCount,
            firstUser is null ? "(no user turn)" : Trim(firstUser, 80));
        return Task.CompletedTask;
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
