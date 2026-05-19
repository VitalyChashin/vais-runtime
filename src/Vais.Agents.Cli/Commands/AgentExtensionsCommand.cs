// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Vais.Agents.Control.Http;

namespace Vais.Agents.Cli.Commands;

/// <summary>
/// Shows the extension handler chain for a specific agent, including scope match
/// diagnostics (which handlers matched, which were excluded, and why).
/// Accepts <c>agent/&lt;id&gt;</c> or bare <c>&lt;id&gt;</c> as the argument.
/// </summary>
internal sealed class AgentExtensionsCommand : AsyncCommand<AgentExtensionsCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Agent id (with or without 'agent/' prefix).")]
        [CommandArgument(0, "<id>")]
        public string Id { get; init; } = string.Empty;

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
        // Accept both "agent/<id>" and bare "<id>" formats.
        var agentId = settings.Id.StartsWith("agent/", StringComparison.OrdinalIgnoreCase)
            ? settings.Id["agent/".Length..]
            : settings.Id;

        var config = VaisConfigFile.LoadOrDefault();
        var client = ClientFactory.Create(config, settings.Context, settings.Token);

        try
        {
            var response = await client.GetAgentExtensionsAsync(agentId, cancellationToken);
            if (response is null)
            {
                AnsiConsole.MarkupLine(
                    $"[red]Agent '{Markup.Escape(agentId)}' not found or extension runtime not available.[/]");
                return 1;
            }

            var format = OutputFormatter.Parse(settings.Output, OutputFormat.Table);
            if (format == OutputFormat.Table)
                RenderTable(response);
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

    private static void RenderTable(AgentExtensionChainResponse response)
    {
        if (response.Handlers.Count == 0)
        {
            AnsiConsole.MarkupLine(
                $"[grey]Agent '{Markup.Escape(response.AgentId)}' has no extension handlers bound.[/]");
            return;
        }

        var table = new Table()
            .AddColumn("EXTENSION")
            .AddColumn("HANDLER")
            .AddColumn("SEAM")
            .AddColumn("PRI")
            .AddColumn("FAILURE")
            .AddColumn("SCOPE MATCHED")
            .AddColumn("SCOPE");

        foreach (var h in response.Handlers)
        {
            var matchMarkup = h.MatchedScope ? "[green]yes[/]" : "[grey]no[/]";
            table.AddRow(
                Markup.Escape(h.ExtensionId),
                Markup.Escape(h.HandlerId),
                Markup.Escape(h.Seam),
                h.Priority.ToString(),
                Markup.Escape(h.FailureMode),
                matchMarkup,
                Markup.Escape(h.ScopeSummary ?? "cluster-wide"));
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine(string.Empty);
        AnsiConsole.MarkupLine("[grey]Matched handlers are included in the agent's invocation chain.[/]");
    }
}
