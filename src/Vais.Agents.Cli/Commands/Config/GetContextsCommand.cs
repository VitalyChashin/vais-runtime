// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Spectre.Console;
using Spectre.Console.Cli;

namespace Vais.Agents.Cli.Commands.Config;

/// <summary>Lists all configured contexts, marking the active one with <c>*</c>.</summary>
internal sealed class GetContextsCommand : Command
{
    protected override int Execute(CommandContext context, CancellationToken cancellationToken)
    {
        var config = VaisConfigFile.LoadOrDefault();
        if (config.Contexts.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No contexts configured.[/] Run `vais config set-context <name> --server <url> --token <jwt>`.");
            return 0;
        }

        var table = new Table()
            .AddColumn("CURRENT")
            .AddColumn("NAME")
            .AddColumn("CLUSTER")
            .AddColumn("USER");
        foreach (var ctx in config.Contexts)
        {
            var marker = string.Equals(ctx.Name, config.CurrentContext, StringComparison.Ordinal) ? "*" : "";
            table.AddRow(marker, ctx.Name, ctx.Cluster, ctx.User);
        }
        AnsiConsole.Write(table);
        return 0;
    }
}
