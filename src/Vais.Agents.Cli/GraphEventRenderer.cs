// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Spectre.Console;

namespace Vais.Agents.Cli;

/// <summary>
/// Per-subtype renderer for the <see cref="AgentGraphEvent"/> closed hierarchy.
/// Used by <c>vais invoke-graph --stream</c> and <c>vais graph-logs</c> to print
/// SSE events with colour and kind prefixes.
/// </summary>
internal sealed class GraphEventRenderer
{
    private readonly IAnsiConsole _console;

    public GraphEventRenderer(IAnsiConsole console)
    {
        _console = console ?? throw new ArgumentNullException(nameof(console));
    }

    public void Render(AgentGraphEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        switch (evt)
        {
            case GraphStarted s:
                _console.MarkupLine($"[green]▶ graph.started[/] [grey]{s.At:HH:mm:ss.fff}[/] [dim]{EscapeMarkup(s.GraphId)} v{EscapeMarkup(s.GraphVersion)} run={EscapeMarkup(s.RunId)}[/]");
                break;
            case NodeStarted ns:
                _console.MarkupLine($"[blue]▷ node.started[/] [grey]{ns.At:HH:mm:ss.fff}[/] step={ns.SuperStep} {EscapeMarkup(ns.NodeId)} ({EscapeMarkup(ns.NodeKind)})");
                break;
            case NodeCompleted nc:
                _console.MarkupLine($"[blue]◁ node.completed[/] [grey]{nc.At:HH:mm:ss.fff}[/] step={nc.SuperStep} {EscapeMarkup(nc.NodeId)} ({nc.Duration.TotalMilliseconds:F0}ms)");
                break;
            case EdgeTraversed et:
                _console.MarkupLine($"[cyan]→ edge.traversed[/] [grey]{et.At:HH:mm:ss.fff}[/] step={et.SuperStep} {EscapeMarkup(et.From)} → {EscapeMarkup(et.To)}");
                break;
            case StateUpdated su:
                var keys = string.Join(", ", su.ChangedKeys.Select(EscapeMarkup));
                _console.MarkupLine($"[dim]~ state.updated[/] [grey]{su.At:HH:mm:ss.fff}[/] step={su.SuperStep} [[{keys}]]");
                break;
            case GraphInterrupted gi:
                _console.MarkupLine($"[yellow]! graph.interrupted[/] [grey]{gi.At:HH:mm:ss.fff}[/] step={gi.SuperStep} node={EscapeMarkup(gi.NodeId)} interruptId={EscapeMarkup(gi.InterruptId)} {(gi.Reason is null ? string.Empty : EscapeMarkup(gi.Reason))}");
                break;
            case GraphResumed gr:
                _console.MarkupLine($"[green]↺ graph.resumed[/] [grey]{gr.At:HH:mm:ss.fff}[/] step={gr.SuperStep} from={EscapeMarkup(gr.ResumedFromNodeId)}");
                break;
            case GraphCompleted gc:
                _console.MarkupLine($"[green]■ graph.completed[/] [grey]{gc.At:HH:mm:ss.fff}[/] step={gc.SuperStep} final={EscapeMarkup(gc.FinalNodeId)} ({gc.Duration.TotalMilliseconds:F0}ms)");
                break;
            case GraphFailed gf:
                _console.MarkupLine($"[red]■ graph.failed[/] [grey]{gf.At:HH:mm:ss.fff}[/] step={gf.SuperStep} {EscapeMarkup(gf.ErrorType)}: {EscapeMarkup(gf.ErrorMessage)}");
                break;
            default:
                _console.MarkupLine($"[grey]?[/] {EscapeMarkup(evt.GetType().Name)}");
                break;
        }
    }

    internal static string EventKindName(AgentGraphEvent evt) => evt switch
    {
        GraphStarted _ => "graph.started",
        NodeStarted _ => "node.started",
        NodeCompleted _ => "node.completed",
        EdgeTraversed _ => "edge.traversed",
        StateUpdated _ => "state.updated",
        GraphInterrupted _ => "graph.interrupted",
        GraphResumed _ => "graph.resumed",
        GraphCompleted _ => "graph.completed",
        GraphFailed _ => "graph.failed",
        _ => evt.GetType().Name,
    };

    private static string EscapeMarkup(string? value) =>
        (value ?? string.Empty).Replace("[", "[[", StringComparison.Ordinal).Replace("]", "]]", StringComparison.Ordinal);
}
