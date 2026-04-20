// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Spectre.Console;
using Spectre.Console.Cli;

namespace Vais.Agents.Cli.Commands;

/// <summary>Prints the CLI assembly version.</summary>
internal sealed class VersionCommand : Command
{
    protected override int Execute(CommandContext context, CancellationToken cancellationToken)
    {
        var version = typeof(VersionCommand).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        AnsiConsole.MarkupLine($"[bold]vais[/] [grey]v{version}[/]");
        return 0;
    }
}
