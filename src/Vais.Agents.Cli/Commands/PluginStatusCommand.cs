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
                var replicas = await FetchKubernetesReplicasAsync(response.Items, cancellationToken);
                RenderTable(response.Items, replicas);
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

    private static async Task<Dictionary<string, string>> FetchKubernetesReplicasAsync(
        IReadOnlyList<PluginInfo> items, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var tasks = items
            .Where(p => p.Kind == PluginKind.Container
                        && p.Topology == "kubernetes"
                        && p.KubernetesDeploymentName is not null
                        && p.KubernetesNamespace is not null)
            .Select(async p =>
            {
                var args = $"get deployment {p.KubernetesDeploymentName} -n {p.KubernetesNamespace}" +
                           " -o jsonpath={.status.readyReplicas}/{.spec.replicas}";
                var output = await KubectlRunner.GetOutputAsync(args, ct);
                result[p.Name] = output ?? "-";
            });
        await Task.WhenAll(tasks);
        return result;
    }

    private static void RenderTable(IReadOnlyList<PluginInfo> items, Dictionary<string, string> replicas)
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
            .AddColumn("TOPOLOGY")
            .AddColumn("REPLICAS")
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
            var topology = p.Topology is not null ? Markup.Escape(p.Topology) : "[grey]-[/]";
            var replicaDisplay = replicas.TryGetValue(p.Name, out var r) ? r : "-";

            table.AddRow(
                Markup.Escape(p.Name),
                kindLabel,
                image,
                topology,
                replicaDisplay,
                stateMarkup,
                Markup.Escape(p.TargetApiVersion),
                Markup.Escape(handlersOrTools),
                pid);

            if (!string.IsNullOrWhiteSpace(p.LastErrorSnippet))
            {
                var snippet = Markup.Escape(p.LastErrorSnippet.Replace("\n", " ↵ "));
                table.AddRow(
                    string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
                    "[dim]last error:[/]", $"[grey]{snippet}[/]", string.Empty, string.Empty);
            }
        }

        AnsiConsole.Write(table);
    }
}
