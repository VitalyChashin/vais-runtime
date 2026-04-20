// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Vais.Agents.Control.Http;

namespace Vais.Agents.Cli.Commands;

/// <summary>
/// Live-run SSE attach. Opens an <c>InvokeStreamEventsAsync</c> stream
/// and prints the full <see cref="AgentEvent"/> taxonomy as events
/// arrive. Ctrl-C sends cancellation through a linked
/// <see cref="CancellationTokenSource"/> and exits with POSIX <c>130</c>.
/// </summary>
/// <remarks>
/// <b>v0.15 limitation</b>: audit-log query (<c>vais audit</c>) and
/// journal replay (<c>--runId</c>) are deferred — they need runtime
/// endpoints not shipped today. <c>--since</c> is a client-side
/// filter only.
/// </remarks>
internal sealed class LogsCommand : AsyncCommand<LogsCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Agent id whose run events to tail.")]
        [CommandArgument(0, "<id>")]
        public required string AgentId { get; init; }

        [Description("Session id to attach to (optional — SSE subscribes to the configured agent without filtering otherwise).")]
        [CommandOption("--session")]
        public string? SessionId { get; init; }

        [Description("Version to target.")]
        [CommandOption("--version")]
        public string? Version { get; init; }

        [Description("Comma-separated event-kind filter (e.g. --only turn.started,tool.completed). Unknown kinds ignored.")]
        [CommandOption("--only")]
        public string? Only { get; init; }

        [Description("Client-side filter: only render events with At >= the supplied ISO-8601 timestamp.")]
        [CommandOption("--since")]
        public string? Since { get; init; }

        [Description("Override the active context.")]
        [CommandOption("--context")]
        public string? Context { get; init; }

        [Description("Bearer token override (wins over VAIS_TOKEN + config).")]
        [CommandOption("--token")]
        public string? Token { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        DateTimeOffset? since = null;
        if (!string.IsNullOrWhiteSpace(settings.Since))
        {
            if (!DateTimeOffset.TryParse(settings.Since, out var parsed))
            {
                AnsiConsole.MarkupLine($"[red]error[/] --since must be ISO-8601; got '{settings.Since}'");
                return ProblemDetailsParser.ExitUsageError;
            }
            since = parsed;
        }

        var kindFilter = ParseKindFilter(settings.Only);

        var config = VaisConfigFile.LoadOrDefault();
        var client = ClientFactory.Create(config, settings.Context, settings.Token);

        // Empty text; session-attach. The runtime opens an SSE stream
        // on the session's next events (v0.12 contract).
        var request = new AgentInvocationRequest(Text: string.Empty, SessionId: settings.SessionId);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            var renderer = new EventRenderer(AnsiConsole.Console);
            await foreach (var evt in client.InvokeStreamEventsAsync(settings.AgentId, request, settings.Version, idempotencyKey: null, cts.Token))
            {
                if (since is { } s && evt.At < s)
                {
                    continue;
                }
                if (kindFilter is not null && !kindFilter.Contains(EventKindName(evt)))
                {
                    continue;
                }
                renderer.Render(evt);
            }
            renderer.FlushAccumulated();
            return ProblemDetailsParser.ExitSuccess;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            AnsiConsole.MarkupLine("[yellow]interrupted[/]");
            return ProblemDetailsParser.ExitSigInt;
        }
        catch (AgentControlPlaneException ex)
        {
            return ProblemDetailsParser.HandleAndExitCode(ex, AnsiConsole.Console);
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    /// <summary>Parse a comma-separated event-kind list into a set. Returns null when the input is null/empty.</summary>
    internal static HashSet<string>? ParseKindFilter(string? only)
    {
        if (string.IsNullOrWhiteSpace(only))
        {
            return null;
        }
        var kinds = only
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0);
        return new HashSet<string>(kinds, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Return the kebab-cased wire event name for an event instance.</summary>
    internal static string EventKindName(AgentEvent evt) => evt switch
    {
        TurnStarted _ => "turn.started",
        TurnCompleted _ => "turn.completed",
        TurnFailed _ => "turn.failed",
        ToolCallStarted _ => "tool.started",
        ToolCallCompleted _ => "tool.completed",
        ToolCallReplayed _ => "tool.replayed",
        GuardrailTriggered _ => "guardrail.triggered",
        InterruptRaised _ => "interrupt.raised",
        HandoffRequested _ => "handoff.requested",
        CompletionDelta _ => "delta",
        _ => evt.GetType().Name,
    };
}
