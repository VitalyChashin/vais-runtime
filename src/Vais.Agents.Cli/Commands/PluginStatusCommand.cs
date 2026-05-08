// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Vais.Agents.Control.Http;

namespace Vais.Agents.Cli.Commands;

/// <summary>
/// Lists loaded plugins (both .NET assembly and Python subprocess) via <c>GET /v1/plugins</c>.
/// Shows plugin name, kind, lifecycle state, API version, handler / tool names, and (for
/// failed Python plugins) the last stderr snippet.
/// </summary>
internal sealed class PluginStatusCommand : AsyncCommand<PluginStatusCommand.Settings>
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
            var response = await client.ListPluginsAsync(cancellationToken);
            var format = OutputFormatter.Parse(settings.Output, OutputFormat.Table);

            if (format == OutputFormat.Table)
            {
                RenderTable(response.Items);
            }
            else if (format == OutputFormat.Json)
            {
                OutputFormatter.WriteJson(response, AnsiConsole.Console);
            }
            else
            {
                OutputFormatter.WriteYaml(response, AnsiConsole.Console);
            }

            return ProblemDetailsParser.ExitSuccess;
        }
        catch (AgentControlPlaneException ex)
        {
            return ProblemDetailsParser.HandleAndExitCode(ex, AnsiConsole.Console);
        }
    }

    private static void RenderTable(IReadOnlyList<PluginInfo> items)
    {
        if (items.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey](no plugins loaded)[/]");
            return;
        }

        var table = new Table()
            .AddColumn("NAME")
            .AddColumn("KIND")
            .AddColumn("IMAGE")
            .AddColumn("STATE")
            .AddColumn("API VERSION")
            .AddColumn("HANDLERS / TOOLS")
            .AddColumn("PID");

        foreach (var p in items)
        {
            var stateMarkup = p.State switch
            {
                PluginState.Ready => "[green]ready[/]",
                PluginState.Loading => "[yellow]loading[/]",
                PluginState.Restarting => "[yellow]restarting[/]",
                PluginState.Unavailable => "[red]unavailable[/]",
                _ => p.State.ToString().ToLowerInvariant(),
            };

            var kindLabel = p.Kind switch
            {
                PluginKind.Python => "python",
                PluginKind.Container => "container",
                _ => "assembly",
            };

            var handlersOrTools = p.Kind == PluginKind.Python && p.ToolNames is { Count: > 0 }
                ? string.Join(", ", p.ToolNames)
                : string.Join(", ", p.Handlers);

            var pid = p.ProcessId?.ToString() ?? "-";
            var image = p.Image is not null ? Markup.Escape(p.Image) : "[grey]-[/]";

            table.AddRow(
                Markup.Escape(p.Name),
                kindLabel,
                image,
                stateMarkup,
                Markup.Escape(p.TargetApiVersion),
                Markup.Escape(handlersOrTools),
                pid);

            if (!string.IsNullOrWhiteSpace(p.LastErrorSnippet))
            {
                var snippet = Markup.Escape(p.LastErrorSnippet.Replace("\n", " ↵ "));
                table.AddRow(string.Empty, string.Empty, string.Empty, "[dim]last error:[/]", $"[grey]{snippet}[/]", string.Empty, string.Empty);
            }
        }

        AnsiConsole.Write(table);
    }
}
