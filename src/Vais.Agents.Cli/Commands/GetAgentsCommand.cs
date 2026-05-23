// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Vais.Agents.Control.Http;

namespace Vais.Agents.Cli.Commands;

/// <summary>
/// Lists manifests via <c>IAgentControlPlaneClient.ListAsync</c> or
/// fetches a single manifest via <c>QueryAsync</c>. Default output
/// is table for list, YAML for single-item — matches kubectl idiom.
/// </summary>
internal sealed class GetAgentsCommand : AsyncCommand<GetAgentsCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Optional agent id. When omitted, lists all agents.")]
        [CommandArgument(0, "[name]")]
        public string? Name { get; init; }

        [Description("Version to target when fetching a single agent.")]
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
                var manifests = await client.ListAsync(settings.LabelPrefix, settings.Limit, cancellationToken);
                if (format == OutputFormat.Table)
                {
                    RenderTable(manifests);
                }
                else
                {
                    OutputFormatter.WriteManifestEnvelopeList(manifests, "Agent", format, AnsiConsole.Console);
                }
                return ProblemDetailsParser.ExitSuccess;
            }

            var response = await client.QueryAsync(settings.Name, settings.Version, cancellationToken);
            if (response is null)
            {
                AnsiConsole.MarkupLine($"[red]error[/] agent '{settings.Name}' not found");
                return ProblemDetailsParser.ExitApiError;
            }

            var singleFormat = OutputFormatter.Parse(settings.Output, OutputFormat.Yaml);
            if (singleFormat == OutputFormat.Table)
            {
                RenderTable(new[] { response.Manifest });
            }
            else
            {
                OutputFormatter.WriteManifestEnvelope(response.Manifest, "Agent", singleFormat, AnsiConsole.Console);
            }
            return ProblemDetailsParser.ExitSuccess;
        }
        catch (AgentControlPlaneException ex)
        {
            return ProblemDetailsParser.HandleAndExitCode(ex, AnsiConsole.Console);
        }
    }

    private static void RenderTable(IReadOnlyList<AgentManifest> manifests)
    {
        if (manifests.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey](no agents)[/]");
            return;
        }
        var table = new Table()
            .AddColumn("ID")
            .AddColumn("VERSION")
            .AddColumn("DESCRIPTION")
            .AddColumn("LABELS");
        foreach (var m in manifests)
        {
            var labels = m.Labels is null || m.Labels.Count == 0
                ? "-"
                : string.Join(",", m.Labels.Select(kv => $"{kv.Key}={kv.Value}"));
            table.AddRow(m.Id, m.Version, m.Description ?? "-", labels);
        }
        AnsiConsole.Write(table);
    }
}
