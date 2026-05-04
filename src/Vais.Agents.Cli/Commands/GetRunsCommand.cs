// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Vais.Agents.Control.Http;

namespace Vais.Agents.Cli.Commands;

/// <summary>
/// Fetches historical run data from the run store via the control-plane REST surface.
/// Without [run-id]: lists runs for a graph. With [run-id]: shows a single run + its nodes.
/// With --node: shows a single node execution in full detail.
/// </summary>
internal sealed class GetRunsCommand : AsyncCommand<GetRunsCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Graph id whose runs to fetch.")]
        [CommandArgument(0, "<graph-id>")]
        public required string GraphId { get; init; }

        [Description("Run id. When supplied, shows the single run and its node executions.")]
        [CommandArgument(1, "[run-id]")]
        public string? RunId { get; init; }

        [Description("Filter list by status: running, completed, failed, interrupted.")]
        [CommandOption("--status")]
        public string? Status { get; init; }

        [Description("Only return runs started at or after this ISO 8601 timestamp.")]
        [CommandOption("--since")]
        public string? Since { get; init; }

        [Description("Only return runs started before this ISO 8601 timestamp.")]
        [CommandOption("--until")]
        public string? Until { get; init; }

        [Description("Maximum number of runs to return (default 20).")]
        [CommandOption("--limit")]
        public int Limit { get; init; } = 20;

        [Description("Show only this node id (requires [run-id]).")]
        [CommandOption("--node")]
        public string? NodeId { get; init; }

        [Description("Output format: table | json. Default: table.")]
        [CommandOption("-o|--output")]
        public string? Output { get; init; }

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
            if (!string.IsNullOrWhiteSpace(settings.RunId))
            {
                if (!string.IsNullOrWhiteSpace(settings.NodeId))
                    return await ShowNodeAsync(client, settings, cancellationToken);

                return await ShowRunAsync(client, settings, cancellationToken);
            }

            return await ListRunsAsync(client, settings, cancellationToken);
        }
        catch (AgentControlPlaneException ex)
        {
            return ProblemDetailsParser.HandleAndExitCode(ex, AnsiConsole.Console);
        }
    }

    private static async Task<int> ListRunsAsync(IAgentControlPlaneClient client, Settings settings, CancellationToken ct)
    {
        DateTimeOffset? since = TryParseOffset(settings.Since);
        DateTimeOffset? until = TryParseOffset(settings.Until);

        var response = await client.ListRunsAsync(settings.GraphId, settings.Status, since, until, settings.Limit, ct);

        var format = OutputFormatter.Parse(settings.Output, OutputFormat.Table);
        if (format == OutputFormat.Json)
        {
            OutputFormatter.WriteJson(response, AnsiConsole.Console);
            return ProblemDetailsParser.ExitSuccess;
        }

        if (response.Items.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey](no runs)[/]");
            return ProblemDetailsParser.ExitSuccess;
        }

        var table = new Table()
            .AddColumn("RUN_ID")
            .AddColumn("STATUS")
            .AddColumn("STARTED_AT")
            .AddColumn("DURATION_MS")
            .AddColumn("STEPS")
            .AddColumn("ERROR");

        foreach (var run in response.Items)
        {
            table.AddRow(
                run.RunId,
                StatusMarkup(run.Status),
                run.StartedAt.ToString("u"),
                run.DurationMs?.ToString() ?? "-",
                run.SuperSteps.ToString(),
                Truncate(run.Error, 40) ?? "-");
        }

        AnsiConsole.Write(table);
        return ProblemDetailsParser.ExitSuccess;
    }

    private static async Task<int> ShowRunAsync(IAgentControlPlaneClient client, Settings settings, CancellationToken ct)
    {
        var run = await client.GetRunAsync(settings.GraphId, settings.RunId!, ct);
        if (run is null)
        {
            AnsiConsole.MarkupLine($"[red]error[/] run '{settings.RunId}' not found");
            return ProblemDetailsParser.ExitApiError;
        }

        var format = OutputFormatter.Parse(settings.Output, OutputFormat.Table);
        if (format == OutputFormat.Json)
        {
            var nodes = await client.GetRunNodesAsync(settings.GraphId, settings.RunId!, ct);
            OutputFormatter.WriteJson(new { run, nodes }, AnsiConsole.Console);
            return ProblemDetailsParser.ExitSuccess;
        }

        AnsiConsole.MarkupLine($"[bold]{run.RunId}[/]  {StatusMarkup(run.Status)}  {run.StartedAt:u}" +
            (run.DurationMs.HasValue ? $"  ({run.DurationMs}ms)" : string.Empty) +
            (run.Error is not null ? $"\n[red]{run.Error}[/]" : string.Empty));

        var nodeList = await client.GetRunNodesAsync(settings.GraphId, settings.RunId!, ct);
        if (nodeList.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey](no nodes recorded)[/]");
            return ProblemDetailsParser.ExitSuccess;
        }

        var nodeTable = new Table()
            .AddColumn("NODE_ID")
            .AddColumn("KIND")
            .AddColumn("STATUS")
            .AddColumn("DURATION_MS")
            .AddColumn("IN_TOK")
            .AddColumn("OUT_TOK")
            .AddColumn("EDGES");

        foreach (var n in nodeList)
        {
            nodeTable.AddRow(
                n.NodeId,
                n.NodeKind,
                StatusMarkup(n.Status),
                n.DurationMs?.ToString() ?? "-",
                n.InputTokens.ToString(),
                n.OutputTokens.ToString(),
                n.EdgesTaken is { Count: > 0 } e ? string.Join(", ", e) : "-");
        }

        AnsiConsole.Write(nodeTable);
        return ProblemDetailsParser.ExitSuccess;
    }

    private static async Task<int> ShowNodeAsync(IAgentControlPlaneClient client, Settings settings, CancellationToken ct)
    {
        var node = await client.GetRunNodeAsync(settings.GraphId, settings.RunId!, settings.NodeId!, ct);
        if (node is null)
        {
            AnsiConsole.MarkupLine($"[red]error[/] node '{settings.NodeId}' not found in run '{settings.RunId}'");
            return ProblemDetailsParser.ExitApiError;
        }

        var format = OutputFormatter.Parse(settings.Output, OutputFormat.Table);
        if (format == OutputFormat.Json)
        {
            OutputFormatter.WriteJson(node, AnsiConsole.Console);
            return ProblemDetailsParser.ExitSuccess;
        }

        AnsiConsole.MarkupLine($"[bold]{node.NodeId}[/] ({node.NodeKind})  {StatusMarkup(node.Status)}");
        if (node.AgentId is not null) AnsiConsole.MarkupLine($"  agent: {node.AgentId}");
        AnsiConsole.MarkupLine($"  started: {node.StartedAt:u}" + (node.DurationMs.HasValue ? $"  ({node.DurationMs}ms)" : string.Empty));
        AnsiConsole.MarkupLine($"  tokens: {node.InputTokens} in / {node.OutputTokens} out");
        if (node.EdgesTaken is { Count: > 0 } edges)
            AnsiConsole.MarkupLine($"  edges: {string.Join(", ", edges)}");
        if (node.Error is not null)
            AnsiConsole.MarkupLine($"  [red]error:[/] {node.Error}");
        if (node.InputText is not null)
        {
            AnsiConsole.MarkupLine("[dim]── input ──────────────────────────────[/]");
            AnsiConsole.WriteLine(node.InputText);
        }
        if (node.OutputText is not null)
        {
            AnsiConsole.MarkupLine("[dim]── output ─────────────────────────────[/]");
            AnsiConsole.WriteLine(node.OutputText);
        }

        return ProblemDetailsParser.ExitSuccess;
    }

    private static string StatusMarkup(string status) => status switch
    {
        "completed" => "[green]completed[/]",
        "failed" => "[red]failed[/]",
        "interrupted" => "[yellow]interrupted[/]",
        "running" => "[blue]running[/]",
        _ => status,
    };

    private static string? Truncate(string? s, int max) =>
        s is null ? null : s.Length <= max ? s : s[..max] + "…";

    private static DateTimeOffset? TryParseOffset(string? value) =>
        DateTimeOffset.TryParse(value, out var result) ? result : null;
}
