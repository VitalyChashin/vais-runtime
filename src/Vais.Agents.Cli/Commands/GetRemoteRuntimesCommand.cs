// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Vais.Agents.Control.Http;

namespace Vais.Agents.Cli.Commands;

/// <summary>
/// Lists remote runtimes configured on the target host via <c>GET /v1/runtimes</c>.
/// Credentials are never surfaced — only URL and identity mode are shown.
/// </summary>
internal sealed class GetRemoteRuntimesCommand : AsyncCommand<GetRemoteRuntimesCommand.Settings>
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
            var response = await client.GetRemoteRuntimesAsync(cancellationToken);
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

    private static void RenderTable(IReadOnlyList<RuntimeInfo> items)
    {
        if (items.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey](no remote runtimes configured)[/]");
            return;
        }
        var table = new Table()
            .AddColumn("URL")
            .AddColumn("IDENTITY MODE");
        foreach (var r in items)
        {
            table.AddRow(r.Url, r.IdentityMode);
        }
        AnsiConsole.Write(table);
    }
}
