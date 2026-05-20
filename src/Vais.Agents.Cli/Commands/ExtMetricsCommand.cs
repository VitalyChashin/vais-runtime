// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Vais.Agents.Control.Http;

namespace Vais.Agents.Cli.Commands;

/// <summary>
/// Shows per-handler latency metrics (p50/p95) for a loaded extension
/// via <c>GET /v1/extensions/{name}/metrics</c>.
/// </summary>
internal sealed class ExtMetricsCommand : AsyncCommand<ExtMetricsCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Extension id.")]
        [CommandArgument(0, "<name>")]
        public string Name { get; init; } = string.Empty;

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
            var response = await client.GetExtensionMetricsAsync(settings.Name, cancellationToken);
            if (response is null)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]No metrics recorded for extension '{Markup.Escape(settings.Name)}' in the current 5-minute window.[/]");
                return ProblemDetailsParser.ExitSuccess;
            }

            RenderTable(response);
            return ProblemDetailsParser.ExitSuccess;
        }
        catch (AgentControlPlaneException ex)
        {
            return ProblemDetailsParser.HandleAndExitCode(ex, AnsiConsole.Console);
        }
    }

    private static void RenderTable(ExtensionMetricsResponse response)
    {
        var table = new Table()
            .Title(Markup.Escape($"Extension: {response.ExtensionId}  (5-minute window)"))
            .AddColumn("HANDLER")
            .AddColumn("SEAM")
            .AddColumn("P50 (s)")
            .AddColumn("P95 (s)")
            .AddColumn("ERROR %")
            .AddColumn("INVOCATIONS");

        foreach (var h in response.Handlers)
        {
            table.AddRow(
                Markup.Escape(h.HandlerId),
                Markup.Escape(h.Seam),
                h.P50Seconds.ToString("F3"),
                h.P95Seconds.ToString("F3"),
                (h.ErrorRate * 100).ToString("F1"),
                h.TotalInvocations.ToString());
        }

        AnsiConsole.Write(table);
    }
}
