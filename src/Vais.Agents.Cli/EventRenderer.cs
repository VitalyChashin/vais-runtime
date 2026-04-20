// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Spectre.Console;

namespace Vais.Agents.Cli;

/// <summary>
/// Per-subtype renderer for the <see cref="AgentEvent"/> closed
/// hierarchy. Used by <c>vais logs</c> + the streaming <c>vais invoke</c>
/// path to print SSE events with colour, kind prefixes, and
/// <see cref="CompletionDelta"/> text accumulation (so assistant
/// turns print as coherent runs rather than one line per chunk).
/// </summary>
internal sealed class EventRenderer
{
    private readonly IAnsiConsole _console;
    private bool _accumulating;
    private readonly System.Text.StringBuilder _buffer = new();

    public EventRenderer(IAnsiConsole console)
    {
        _console = console ?? throw new ArgumentNullException(nameof(console));
    }

    public void Render(AgentEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        switch (evt)
        {
            case CompletionDelta delta:
                Accumulate(delta);
                break;
            case TurnStarted turn:
                FlushAccumulated();
                _console.MarkupLine($"[green]\u25b6 turn.started[/] [grey]{turn.At:HH:mm:ss.fff}[/] [dim]{EscapeMarkup(turn.UserMessage)}[/]");
                break;
            case TurnCompleted completed:
                FlushAccumulated();
                _console.MarkupLine($"[green]\u25a0 turn.completed[/] [grey]{completed.At:HH:mm:ss.fff}[/] [dim]({completed.PromptTokens ?? 0}+{completed.CompletionTokens ?? 0} tokens, {completed.Duration.TotalMilliseconds:F0}ms)[/]");
                break;
            case TurnFailed failed:
                FlushAccumulated();
                _console.MarkupLine($"[red]\u25a0 turn.failed[/] [grey]{failed.At:HH:mm:ss.fff}[/] {EscapeMarkup(failed.ErrorType)}: {EscapeMarkup(failed.ErrorMessage)}");
                break;
            case ToolCallStarted tool:
                FlushAccumulated();
                _console.MarkupLine($"[blue]\u25ba tool.started[/] [grey]{tool.At:HH:mm:ss.fff}[/] {EscapeMarkup(tool.ToolName)} (call {EscapeMarkup(tool.CallId)})");
                break;
            case ToolCallCompleted tc:
                FlushAccumulated();
                var label = tc.Succeeded ? "[blue]\u25c4 tool.completed[/]" : "[red]\u25c4 tool.failed[/]";
                _console.MarkupLine($"{label} [grey]{tc.At:HH:mm:ss.fff}[/] {EscapeMarkup(tc.ToolName)} ({tc.Duration.TotalMilliseconds:F0}ms)");
                break;
            case ToolCallReplayed replayed:
                FlushAccumulated();
                _console.MarkupLine($"[cyan]\u25c4 tool.replayed[/] [grey]{replayed.At:HH:mm:ss.fff}[/] {EscapeMarkup(replayed.ToolName)}");
                break;
            case GuardrailTriggered guardrail:
                FlushAccumulated();
                _console.MarkupLine($"[red]! guardrail.triggered[/] [grey]{guardrail.At:HH:mm:ss.fff}[/] {guardrail.Layer}: {EscapeMarkup(guardrail.Reason ?? "(no reason)")}");
                break;
            case InterruptRaised interrupt:
                FlushAccumulated();
                _console.MarkupLine($"[yellow]! interrupt.raised[/] [grey]{interrupt.At:HH:mm:ss.fff}[/] {EscapeMarkup(interrupt.InterruptId)}: {EscapeMarkup(interrupt.Reason)}");
                break;
            case HandoffRequested handoff:
                FlushAccumulated();
                _console.MarkupLine($"[magenta]\u2192 handoff.requested[/] [grey]{handoff.At:HH:mm:ss.fff}[/] {EscapeMarkup(handoff.Handoff.FromAgent)} \u2192 {EscapeMarkup(handoff.Handoff.ToAgent)}");
                break;
            default:
                FlushAccumulated();
                _console.MarkupLine($"[grey]?[/] {evt.GetType().Name}");
                break;
        }
    }

    /// <summary>
    /// Flush any accumulated <see cref="CompletionDelta"/> text.
    /// Called by consumers on stream end, or implicitly by the renderer
    /// on any non-delta event.
    /// </summary>
    public void FlushAccumulated()
    {
        if (!_accumulating)
        {
            return;
        }
        var text = _buffer.ToString();
        if (!string.IsNullOrEmpty(text))
        {
            _console.Write(text);
            _console.WriteLine();
        }
        _buffer.Clear();
        _accumulating = false;
    }

    private void Accumulate(CompletionDelta delta)
    {
        if (!_accumulating)
        {
            _console.Markup("[bold white]assistant:[/] ");
            _accumulating = true;
        }
        _buffer.Append(delta.TextDelta);
    }

    private static string EscapeMarkup(string value) =>
        (value ?? string.Empty).Replace("[", "[[", StringComparison.Ordinal).Replace("]", "]]", StringComparison.Ordinal);
}
