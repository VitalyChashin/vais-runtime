// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.ComponentModel;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Cli;
using Vais.Agents.Control.Http;

namespace Vais.Agents.Cli.Commands.Diagnose;

/// <summary>
/// Prints the mechanical-failure health rollup for a single run. Requires the run-health store
/// to be configured (<c>VAIS_RUN_HEALTH_STORE_CONNECTION</c>); without it the health block is
/// absent and the command reports "run-health store not configured".
/// </summary>
internal sealed class DiagnoseRunCommand : AsyncCommand<DiagnoseRunCommand.Settings>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public sealed class Settings : CommandSettings
    {
        [Description("Graph id that owns the run.")]
        [CommandArgument(0, "<graph-id>")]
        public required string GraphId { get; init; }

        [Description("Run id to diagnose.")]
        [CommandArgument(1, "<run-id>")]
        public required string RunId { get; init; }

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
            var run = await client.GetRunAsync(settings.GraphId, settings.RunId, cancellationToken);
            if (run is null)
            {
                AnsiConsole.MarkupLine($"[red]error[/] run '{settings.RunId}' not found");
                return ProblemDetailsParser.ExitApiError;
            }

            var format = OutputFormatter.Parse(settings.Output, OutputFormat.Table);
            if (format == OutputFormat.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(run.Health, JsonOptions));
                return ProblemDetailsParser.ExitSuccess;
            }

            // ── Header ──────────────────────────────────────────────────────────
            AnsiConsole.MarkupLine($"[bold]{run.RunId}[/]  {RunStatusMarkup(run.Status)}  {run.StartedAt:u}" +
                (run.DurationMs.HasValue ? $"  [dim]({run.DurationMs}ms)[/]" : string.Empty));

            if (run.Health is null)
            {
                AnsiConsole.MarkupLine("[grey]Run-health store not configured (VAIS_RUN_HEALTH_STORE_CONNECTION unset).[/]");
                return ProblemDetailsParser.ExitSuccess;
            }

            // ── Health summary ───────────────────────────────────────────────────
            var levelMarkup = run.Health.Level switch
            {
                "healthy"  => "[green]healthy[/]",
                "degraded" => "[yellow]degraded[/]",
                "failed"   => "[red]failed[/]",
                _          => run.Health.Level,
            };

            AnsiConsole.MarkupLine($"health: {levelMarkup}");

            var allSignals = run.Health.Signals.Concat(run.Health.BackgroundFailures).ToList();
            if (allSignals.Count == 0)
            {
                AnsiConsole.MarkupLine("[grey](no mechanical failures detected)[/]");
                return ProblemDetailsParser.ExitSuccess;
            }

            // ── Signal table ─────────────────────────────────────────────────────
            var table = new Table()
                .AddColumn("SOURCE")
                .AddColumn("KIND")
                .AddColumn("LEVEL")
                .AddColumn("ERROR_TYPE")
                .AddColumn("TRANSIENT")
                .AddColumn("AT");

            foreach (var s in allSignals)
            {
                table.AddRow(
                    Markup.Escape(s.Source),
                    Markup.Escape(s.Kind),
                    SignalLevelMarkup(s.Level),
                    Markup.Escape(s.ErrorType ?? "-"),
                    s.IsTransient ? "[dim]yes[/]" : "no",
                    s.At.ToString("u"));
            }

            AnsiConsole.Write(table);

            if (run.Health.BackgroundFailures.Count > 0)
                AnsiConsole.MarkupLine($"[grey]({run.Health.BackgroundFailures.Count} background sub-run failure(s) included)[/]");

            return ProblemDetailsParser.ExitSuccess;
        }
        catch (AgentControlPlaneException ex)
        {
            return ProblemDetailsParser.HandleAndExitCode(ex, AnsiConsole.Console);
        }
    }

    private static string RunStatusMarkup(string status) => status switch
    {
        "completed"   => "[green]completed[/]",
        "failed"      => "[red]failed[/]",
        "interrupted" => "[yellow]interrupted[/]",
        "running"     => "[blue]running[/]",
        _             => status,
    };

    private static string SignalLevelMarkup(string level) => level switch
    {
        "warning" => "[yellow]warning[/]",
        "error"   => "[red]error[/]",
        _         => level,
    };
}
