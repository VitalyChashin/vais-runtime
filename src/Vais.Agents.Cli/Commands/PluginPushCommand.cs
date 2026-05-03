// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Vais.Agents.Control.Http;

namespace Vais.Agents.Cli.Commands;

/// <summary>
/// Packs a Python plugin source directory into a tar.gz archive and pushes it to the runtime
/// via <c>POST /v1/plugins/{name}/source</c>, triggering a DrainAndSwap reload.
/// </summary>
internal sealed class PluginPushCommand : AsyncCommand<PluginPushCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Name of the Python plugin to reload.")]
        [CommandArgument(0, "<plugin-name>")]
        public string PluginName { get; init; } = "";

        [Description("Directory to pack and push. Defaults to ./src")]
        [CommandOption("--source")]
        public string? Source { get; init; }

        [Description("Override the active context from ~/.vais/config.yaml.")]
        [CommandOption("--context")]
        public string? Context { get; init; }

        [Description("Bearer token override (wins over VAIS_TOKEN + config).")]
        [CommandOption("--token")]
        public string? Token { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var sourceDir = Path.GetFullPath(settings.Source ?? Path.Combine(Directory.GetCurrentDirectory(), "src"));
        if (!Directory.Exists(sourceDir))
        {
            AnsiConsole.MarkupLine($"[red]error[/] source directory not found: {Markup.Escape(sourceDir)}");
            return ProblemDetailsParser.ExitUsageError;
        }

        var config = VaisConfigFile.LoadOrDefault();
        var client = ClientFactory.Create(config, settings.Context, settings.Token);

        try
        {
            PluginSourcePushResponse? result = null;
            await AnsiConsole.Status()
                .StartAsync($"Pushing {settings.PluginName}...", async _ =>
                {
                    using var archive = PluginSourcePacker.Pack(sourceDir);
                    result = await client.PushPluginSourceAsync(settings.PluginName, archive, cancellationToken);
                });

            if (result!.Status == PluginSourcePushStatus.Success)
            {
                var pid = result.ProcessId?.ToString() ?? "?";
                AnsiConsole.MarkupLine($"[green]✓[/] {Markup.Escape(settings.PluginName)} reloaded (PID {pid})");
                return ProblemDetailsParser.ExitSuccess;
            }

            var detail = result.ErrorMessage ?? result.Status.ToString();
            AnsiConsole.MarkupLine($"[red]✗[/] reload failed: {Markup.Escape(result.Status.ToString())} — {Markup.Escape(detail)}");
            return ProblemDetailsParser.ExitApiError;
        }
        catch (AgentControlPlaneException ex)
        {
            return ProblemDetailsParser.HandleAndExitCode(ex, AnsiConsole.Console);
        }
    }
}
