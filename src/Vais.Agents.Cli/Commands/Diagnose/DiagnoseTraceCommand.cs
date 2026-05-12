// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Vais.Agents.Control;
using Vais.Agents.Control.Http;

namespace Vais.Agents.Cli.Commands.Diagnose;

/// <summary>
/// Fetches all spans for a given trace ID from the runtime's in-process span buffer
/// and renders them as a Unicode span tree (newest root first).
/// </summary>
internal sealed class DiagnoseTraceCommand : AsyncCommand<DiagnoseTraceCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Trace ID to display.")]
        [CommandArgument(0, "<trace-id>")]
        public required string TraceId { get; init; }

        [Description("Override the active context from ~/.vais/config.yaml.")]
        [CommandOption("--context")]
        public string? Context { get; init; }

        [Description("Bearer token override (wins over VAIS_TOKEN + config).")]
        [CommandOption("--token")]
        public string? Token { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var config = VaisConfigFile.LoadOrDefault();
        var client = ClientFactory.Create(config, settings.Context, settings.Token);

        try
        {
            // Fetch a large window — filter client-side by traceId
            var response = await client.GetDiagSpansAsync(source: null, limit: 1000, cancellationToken);
            var spans = response.Items
                .Where(s => string.Equals(s.TraceId, settings.TraceId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (spans.Count == 0)
            {
                AnsiConsole.MarkupLine($"[yellow]warn[/] no spans found for trace [bold]{settings.TraceId}[/]");
                return ProblemDetailsParser.ExitSuccess;
            }

            PrintTree(spans);
            return ProblemDetailsParser.ExitSuccess;
        }
        catch (AgentControlPlaneException ex)
        {
            return ProblemDetailsParser.HandleAndExitCode(ex, AnsiConsole.Console);
        }
    }

    private static void PrintTree(IReadOnlyList<DiagSpanRecord> spans)
    {
        var byId = spans.ToDictionary(s => s.SpanId, StringComparer.OrdinalIgnoreCase);
        var children = spans
            .GroupBy(s => s.ParentSpanId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.StartTime).ToList(), StringComparer.OrdinalIgnoreCase);

        var roots = spans
            .Where(s => s.ParentSpanId is null || !byId.ContainsKey(s.ParentSpanId))
            .OrderBy(s => s.StartTime)
            .ToList();

        var tree = new Tree($"[dim]trace[/] [bold]{spans[0].TraceId}[/]");
        foreach (var root in roots)
            AddNode(tree, root, children);

        AnsiConsole.Write(tree);
    }

    private static void AddNode(IHasTreeNodes parent, DiagSpanRecord span, Dictionary<string, List<DiagSpanRecord>> children)
    {
        var label = $"[bold]{Markup.Escape(span.Name)}[/] [dim]{span.SpanId[..8]}[/]  " +
                    $"{StatusMarkup(span.Status)}  {span.DurationMs}ms  " +
                    $"[dim]{span.Source}[/]";
        var node = parent.AddNode(label);

        if (children.TryGetValue(span.SpanId, out var kids))
            foreach (var child in kids)
                AddNode(node, child, children);
    }

    private static string StatusMarkup(string status) => status switch
    {
        "Ok" => "[green]Ok[/]",
        "Error" => "[red]Error[/]",
        _ => $"[grey]{Markup.Escape(status)}[/]",
    };
}
