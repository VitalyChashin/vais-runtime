// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Vais.Agents.Control.Http;

namespace Vais.Agents.Cli.Commands;

/// <summary>
/// Lists graph manifests via <c>IAgentControlPlaneClient.ListGraphsAsync</c> or
/// fetches a single manifest via <c>QueryGraphAsync</c>. Parallel to
/// <see cref="GetAgentsCommand"/> — same ergonomics, graph resource kind.
/// </summary>
internal sealed class GetGraphsCommand : AsyncCommand<GetGraphsCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Optional graph id. When omitted, lists all graphs.")]
        [CommandArgument(0, "[name]")]
        public string? Name { get; init; }

        [Description("Version to target when fetching a single graph.")]
        [CommandOption("--version")]
        public string? Version { get; init; }

        [Description("Filter list results by label prefix (e.g. 'team:').")]
        [CommandOption("--label-prefix")]
        public string? LabelPrefix { get; init; }

        [Description("Maximum number of manifests to return in list mode.")]
        [CommandOption("--limit")]
        public int? Limit { get; init; }

        [Description("Output format: table | yaml | json. Default: table for list, yaml for single-item.")]
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
            if (string.IsNullOrWhiteSpace(settings.Name))
            {
                var format = OutputFormatter.Parse(settings.Output, OutputFormat.Table);
                var response = await client.ListGraphsAsync(settings.LabelPrefix, settings.Limit, cursor: null, cancellationToken);
                if (format == OutputFormat.Table)
                {
                    RenderTable(response.Items);
                }
                else
                {
                    OutputFormatter.WriteManifestEnvelopeList(response.Items, "AgentGraph", format, AnsiConsole.Console);
                }
                return ProblemDetailsParser.ExitSuccess;
            }

            var single = await client.QueryGraphAsync(settings.Name, settings.Version, cancellationToken);
            if (single is null)
            {
                AnsiConsole.MarkupLine($"[red]error[/] graph '{settings.Name}' not found");
                return ProblemDetailsParser.ExitApiError;
            }

            var singleFormat = OutputFormatter.Parse(settings.Output, OutputFormat.Yaml);
            if (singleFormat == OutputFormat.Table)
            {
                RenderTable(new[] { single.Manifest });
            }
            else
            {
                OutputFormatter.WriteManifestEnvelope(single.Manifest, "AgentGraph", singleFormat, AnsiConsole.Console);
            }
            return ProblemDetailsParser.ExitSuccess;
        }
        catch (AgentControlPlaneException ex)
        {
            return ProblemDetailsParser.HandleAndExitCode(ex, AnsiConsole.Console);
        }
    }

    private static void RenderTable(IReadOnlyList<AgentGraphManifest> manifests)
    {
        if (manifests.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey](no graphs)[/]");
            return;
        }
        var table = new Table()
            .AddColumn("ID")
            .AddColumn("VERSION")
            .AddColumn("ENTRY")
            .AddColumn("NODES")
            .AddColumn("DESCRIPTION")
            .AddColumn("LABELS");
        foreach (var m in manifests)
        {
            var labels = m.Labels is null || m.Labels.Count == 0
                ? "-"
                : string.Join(",", m.Labels.Select(kv => $"{kv.Key}={kv.Value}"));
            table.AddRow(m.Id, m.Version, m.Entry, m.Nodes.Count.ToString(), m.Description ?? "-", labels);
        }
        AnsiConsole.Write(table);
    }
}
