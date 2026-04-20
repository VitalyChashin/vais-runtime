// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Spectre.Console;
using Spectre.Console.Cli;

namespace Vais.Agents.Cli.Commands.Config;

/// <summary>Prints the active context name, or a warning when none is selected.</summary>
internal sealed class CurrentContextCommand : Command
{
    protected override int Execute(CommandContext context, CancellationToken cancellationToken)
    {
        var config = VaisConfigFile.LoadOrDefault();
        if (string.IsNullOrWhiteSpace(config.CurrentContext))
        {
            AnsiConsole.MarkupLine("[yellow]No context selected.[/]");
            return 1;
        }
        AnsiConsole.WriteLine(config.CurrentContext);
        return 0;
    }
}
