// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Vais.Agents.Control.Http;

namespace Vais.Agents.Cli.Commands;

/// <summary>
/// <c>vais trajectories list</c> — fetch the Plan D trajectory tee corpus from the runtime
/// control plane. Newest-first; filters AND-combined; PII redacted at tee time so the CLI
/// only ever sees the redacted argument shape, never raw values.
/// </summary>
internal sealed class TrajectoriesListCommand : AsyncCommand<TrajectoriesListCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Filter to a single coordinator agent id.")]
        [CommandOption("--agent")]
        public string? Agent { get; init; }

        [Description("Filter to a single run id.")]
        [CommandOption("--run")]
        public string? Run { get; init; }

        [Description("Filter to a single tool / verb (e.g. tavily_search, vais.validate).")]
        [CommandOption("--concept")]
        public string? Concept { get; init; }

        [Description("Filter to one transport: north | south.")]
        [CommandOption("--transport")]
        public string? Transport { get; init; }

        [Description("Filter to one outcome: Ok | Error | ShortCircuit.")]
        [CommandOption("--outcome")]
        public string? Outcome { get; init; }

        [Description("Only return events at or after this ISO 8601 timestamp (e.g. 2026-05-26T10:00:00Z).")]
        [CommandOption("--since")]
        public string? Since { get; init; }

        [Description("Only return events before this ISO 8601 timestamp.")]
        [CommandOption("--until")]
        public string? Until { get; init; }

        [Description("Maximum number of events to return (default 50, max 500).")]
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

        DateTimeOffset? since = DateTimeOffset.TryParse(settings.Since, out var s) ? s : null;
        DateTimeOffset? until = DateTimeOffset.TryParse(settings.Until, out var u) ? u : null;

        try
        {
            var events = await client.ListTrajectoriesAsync(
                agent: settings.Agent,
                run: settings.Run,
                concept: settings.Concept,
                transport: settings.Transport,
                outcome: settings.Outcome,
                since: since,
                until: until,
                limit: settings.Limit,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var format = OutputFormatter.Parse(settings.Output, OutputFormat.Table);
            if (format == OutputFormat.Json)
            {
                OutputFormatter.WriteJson(events, AnsiConsole.Console);
                return ProblemDetailsParser.ExitSuccess;
            }

            if (events.Count == 0)
            {
                AnsiConsole.MarkupLine("[grey](no trajectory events)[/]");
                return ProblemDetailsParser.ExitSuccess;
            }

            var table = new Table()
                .AddColumn("TIMESTAMP")
                .AddColumn("EVENT")
                .AddColumn("AGENT")
                .AddColumn("CONCEPT")
                .AddColumn("TRANSPORT")
                .AddColumn("OUTCOME")
                .AddColumn("DURATION");

            foreach (var e in events)
            {
                table.AddRow(
                    e.Timestamp.ToString("u"),
                    e.EventName,
                    e.AgentId ?? "-",
                    e.ConceptName ?? "-",
                    e.Transport ?? "-",
                    OutcomeMarkup(e.Outcome),
                    e.Duration.HasValue ? $"{e.Duration.Value.TotalMilliseconds:F0}ms" : "-");
            }

            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine($"[grey]{events.Count} event(s); newest first.[/]");
            return ProblemDetailsParser.ExitSuccess;
        }
        catch (AgentControlPlaneException ex)
        {
            return ProblemDetailsParser.HandleAndExitCode(ex, AnsiConsole.Console);
        }
    }

    private static string OutcomeMarkup(TrajectoryOutcome? outcome) => outcome?.Kind switch
    {
        TrajectoryOutcomeKind.Ok => "[green]ok[/]",
        TrajectoryOutcomeKind.Error => $"[red]error[/] {Markup.Escape(outcome.ErrorType ?? "")}",
        TrajectoryOutcomeKind.ShortCircuit => "[yellow]short-circuit[/]",
        _ => "-",
    };
}
