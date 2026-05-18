// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Vais.Agents.Control.Http;

namespace Vais.Agents.Cli.Commands;

/// <summary>
/// Loads (or hot-reloads) a plugin whose DLL is already present in the runtime's
/// plugins directory. Useful for plugins deployed by external mechanisms such as
/// sidecars, CI/CD pipelines, or manual filesystem copies.
/// </summary>
internal sealed class PluginImportExistingCommand : AsyncCommand<PluginImportExistingCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Plugin name — matches the folder name in the runtime's plugins directory.")]
        [CommandArgument(0, "<name>")]
        public required string Name { get; init; }

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
            PluginDllPushResponse? result = null;
            await AnsiConsole.Status()
                .StartAsync($"Importing {Markup.Escape(settings.Name)}…", async _ =>
                {
                    result = await client.ImportExistingPluginAsync(settings.Name, cancellationToken);
                });

            if (result!.Status is PluginDllPushStatus.Success or PluginDllPushStatus.Bootstrapped)
            {
                var verb = result.Status == PluginDllPushStatus.Bootstrapped ? "bootstrapped" : "imported";
                var handlers = result.Handlers is { Count: > 0 }
                    ? string.Join(", ", result.Handlers)
                    : "—";
                AnsiConsole.MarkupLine($"[green]✓[/] {Markup.Escape(settings.Name)} {verb} (handlers: {Markup.Escape(handlers)})");
                return ProblemDetailsParser.ExitSuccess;
            }

            var detail = result.ErrorMessage ?? result.Status.ToString();
            AnsiConsole.MarkupLine(
                $"[red]✗[/] import failed: {Markup.Escape(result.Status.ToString())} — {Markup.Escape(detail)}");
            return ProblemDetailsParser.ExitApiError;
        }
        catch (AgentControlPlaneException ex)
        {
            return ProblemDetailsParser.HandleAndExitCode(ex, AnsiConsole.Console);
        }
    }
}
