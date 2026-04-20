// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Vais.Agents.Cli.Commands.Config;

/// <summary>Switches the active context. Fails if the named context doesn't exist.</summary>
internal sealed class UseContextCommand : Command<UseContextCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Name of the context to switch to.")]
        [CommandArgument(0, "<name>")]
        public required string Name { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var config = VaisConfigFile.LoadOrDefault();
        var found = VaisConfigFile.FindContext(config, settings.Name);
        if (found is null)
        {
            AnsiConsole.MarkupLine($"[red]Context '{settings.Name}' not found.[/] Run `vais config get-contexts` to list available.");
            return 1;
        }
        config.CurrentContext = settings.Name;
        VaisConfigFile.Save(config);
        AnsiConsole.MarkupLine($"Switched to context [bold]'{settings.Name}'[/].");
        return 0;
    }
}
