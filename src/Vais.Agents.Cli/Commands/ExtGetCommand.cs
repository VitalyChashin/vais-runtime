// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Vais.Agents.Control.Http;

namespace Vais.Agents.Cli.Commands;

/// <summary>
/// Fetches a single loaded extension via <c>GET /v1/extensions/{name}</c>.
/// Default output is YAML (full manifest); use <c>-o json</c> for JSON.
/// </summary>
internal sealed class ExtGetCommand : AsyncCommand<ExtGetCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Extension id.")]
        [CommandArgument(0, "<name>")]
        public string Name { get; init; } = string.Empty;

        [Description("Output format: yaml | json | table. Default: yaml.")]
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
            var response = await client.GetExtensionAsync(settings.Name, cancellationToken);
            if (response is null)
            {
                AnsiConsole.MarkupLine($"[red]Extension '{Markup.Escape(settings.Name)}' is not loaded.[/]");
                return 1;
            }

            var format = OutputFormatter.Parse(settings.Output, OutputFormat.Yaml);
            if (format == OutputFormat.Json)
                OutputFormatter.WriteJson(response, AnsiConsole.Console);
            else if (format == OutputFormat.Table)
                RenderTable(response);
            else
                OutputFormatter.WriteYaml(response, AnsiConsole.Console);

            return ProblemDetailsParser.ExitSuccess;
        }
        catch (AgentControlPlaneException ex)
        {
            return ProblemDetailsParser.HandleAndExitCode(ex, AnsiConsole.Console);
        }
    }

    private static void RenderTable(ExtensionQueryResponse response)
    {
        var e = response.Extension;
        var table = new Table()
            .AddColumn("FIELD")
            .AddColumn("VALUE");

        table.AddRow("id",      Markup.Escape(e.ExtensionId));
        table.AddRow("version", Markup.Escape(e.Version));
        table.AddRow("host",    Markup.Escape(e.Host));
        table.AddRow("handlers", Markup.Escape(
            string.Join(", ", e.Handlers.Select(h => $"{h.HandlerId}({h.Seam}, p={h.Priority}, f={h.FailureMode})"))));

        AnsiConsole.Write(table);
    }
}
