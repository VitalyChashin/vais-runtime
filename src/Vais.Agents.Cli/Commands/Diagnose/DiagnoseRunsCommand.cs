// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.ComponentModel;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Cli;
using Vais.Agents.Control.Http;

namespace Vais.Agents.Cli.Commands.Diagnose;

/// <summary>
/// Part 2c (DM-9) — <c>vais diagnose runs</c> — cross-run rollup. Lists recent runs whose worst
/// persisted mechanical-failure level is at least the requested minimum. Identical results to
/// <c>vais.runHealth</c> on the diagnostic MCP (both call <c>IRunHealthAggregator.ListDegradedRunsAsync</c>).
/// </summary>
internal sealed class DiagnoseRunsCommand : AsyncCommand<DiagnoseRunsCommand.Settings>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public sealed class Settings : CommandSettings
    {
        [Description("Minimum severity: degraded (default) or failed.")]
        [CommandOption("--level")]
        public string? Level { get; init; }

        [Description("ISO-8601 timestamp; default: 24h ago.")]
        [CommandOption("--since")]
        public string? Since { get; init; }

        [Description("Maximum runs to return (default 50, max 200).")]
        [CommandOption("--limit")]
        public int Limit { get; init; } = 50;

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
            var resp = await client.GetRunHealthAsync(settings.Level, settings.Since, settings.Limit, cancellationToken);
            var format = OutputFormatter.Parse(settings.Output, OutputFormat.Table);
            if (format == OutputFormat.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(resp, JsonOptions));
                return ProblemDetailsParser.ExitSuccess;
            }

            if (resp.Count == 0)
            {
                AnsiConsole.MarkupLine("[grey](no degraded/failed runs in the window)[/]");
                return ProblemDetailsParser.ExitSuccess;
            }

            var table = new Table()
                .AddColumn("RUN")
                .AddColumn("LEVEL")
                .AddColumn("SIGNALS")
                .AddColumn("LATEST_AT");
            foreach (var row in resp.Items)
            {
                table.AddRow(
                    Markup.Escape(row.RunId),
                    LevelMarkup(row.Level),
                    row.SignalCount.ToString(),
                    row.LatestAt.ToString("u"));
            }
            AnsiConsole.Write(table);
            return ProblemDetailsParser.ExitSuccess;
        }
        catch (AgentControlPlaneException ex)
        {
            return ProblemDetailsParser.HandleAndExitCode(ex, AnsiConsole.Console);
        }
    }

    private static string LevelMarkup(string level) => level switch
    {
        "healthy" => "[green]healthy[/]",
        "degraded" => "[yellow]degraded[/]",
        "failed" => "[red]failed[/]",
        _ => level,
    };
}
