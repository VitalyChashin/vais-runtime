// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Vais.Agents.Control.Http;

namespace Vais.Agents.Cli.Commands.Diagnose;

/// <summary>
/// Displays per-interface outgoing Orleans grain call counters recorded by
/// <c>OrleansOutgoingActivityFilter</c>.
/// </summary>
internal sealed class DiagnoseFilterStatusCommand : AsyncCommand<DiagnoseFilterStatusCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
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
            var response = await client.GetFilterStatusAsync(cancellationToken);

            var format = OutputFormatter.Parse(settings.Output, OutputFormat.Table);
            if (format == OutputFormat.Json)
            {
                OutputFormatter.WriteJson(response, AnsiConsole.Console);
                return ProblemDetailsParser.ExitSuccess;
            }

            if (response.Calls.Count == 0)
            {
                AnsiConsole.MarkupLine("[grey](no calls recorded)[/]");
                return ProblemDetailsParser.ExitSuccess;
            }

            var table = new Table()
                .AddColumn("GRAIN_INTERFACE")
                .AddColumn(new TableColumn("WITH_ACTIVITY").RightAligned())
                .AddColumn(new TableColumn("WITHOUT_ACTIVITY").RightAligned())
                .AddColumn(new TableColumn("TOTAL").RightAligned());

            foreach (var entry in response.Calls)
            {
                var total = entry.WithActivity + entry.WithoutActivity;
                table.AddRow(
                    Markup.Escape(entry.GrainInterface),
                    entry.WithActivity.ToString(),
                    entry.WithoutActivity.ToString(),
                    total.ToString());
            }

            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine($"[dim]total calls: {response.TotalCalls}[/]");
            return ProblemDetailsParser.ExitSuccess;
        }
        catch (AgentControlPlaneException ex)
        {
            return ProblemDetailsParser.HandleAndExitCode(ex, AnsiConsole.Console);
        }
    }
}
