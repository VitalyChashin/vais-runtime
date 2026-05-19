// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Vais.Agents.Control.Http;

namespace Vais.Agents.Cli.Commands;

/// <summary>
/// Lists loaded extensions via <c>GET /v1/extensions</c>.
/// Shows extension id, version, host, and bound handler/seam pairs.
/// </summary>
internal sealed class ExtListCommand : AsyncCommand<ExtListCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Output format: table | json | yaml. Default: table.")]
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
            var response = await client.ListExtensionsAsync(cancellationToken);
            var format = OutputFormatter.Parse(settings.Output, OutputFormat.Table);

            if (format == OutputFormat.Table)
                RenderTable(response.Items);
            else if (format == OutputFormat.Json)
                OutputFormatter.WriteJson(response, AnsiConsole.Console);
            else
                OutputFormatter.WriteYaml(response, AnsiConsole.Console);

            return ProblemDetailsParser.ExitSuccess;
        }
        catch (AgentControlPlaneException ex)
        {
            return ProblemDetailsParser.HandleAndExitCode(ex, AnsiConsole.Console);
        }
    }

    private static void RenderTable(IReadOnlyList<ExtensionInfo> items)
    {
        if (items.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey](no extensions loaded)[/]");
            return;
        }

        var table = new Table()
            .AddColumn("NAME")
            .AddColumn("VERSION")
            .AddColumn("HOST")
            .AddColumn("HANDLERS");

        foreach (var e in items)
        {
            var handlers = e.Handlers.Count == 0
                ? "[grey]-[/]"
                : Markup.Escape(string.Join(", ", e.Handlers.Select(h => $"{h.HandlerId}({h.Seam}:{h.Priority})")));
            table.AddRow(
                Markup.Escape(e.ExtensionId),
                Markup.Escape(e.Version),
                Markup.Escape(e.Host),
                handlers);
        }

        AnsiConsole.Write(table);
    }
}
