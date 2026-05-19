// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Vais.Agents.Cli.Commands;

/// <summary>
/// Shows container extension logs. For <c>host: container</c> extensions, connect
/// directly to the container using <c>docker logs</c> or <c>kubectl logs</c>.
/// Per-handler structured events flow through the runtime's OTLP receiver when enabled.
/// </summary>
internal sealed class ExtLogsCommand : AsyncCommand<ExtLogsCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Extension id.")]
        [CommandArgument(0, "<name>")]
        public string Name { get; init; } = string.Empty;

        [Description("Number of recent lines to show (container hosts only). Default: 50.")]
        [CommandOption("--tail")]
        public int? Tail { get; init; }

        [Description("Override the active context from ~/.vais/config.yaml.")]
        [CommandOption("--context")]
        public string? Context { get; init; }

        [Description("Bearer token override (wins over VAIS_TOKEN + config).")]
        [CommandOption("--token")]
        public string? Token { get; init; }
    }

    protected override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine($"[yellow]Extension log streaming is not yet available via the CLI.[/]");
        AnsiConsole.MarkupLine(string.Empty);
        AnsiConsole.MarkupLine("For [bold]host:container[/] extensions, view logs directly:");
        AnsiConsole.MarkupLine($"  [dim]docker logs <container-id>[/]");
        AnsiConsole.MarkupLine($"  [dim]kubectl logs <pod-name> -n <namespace>[/]");
        AnsiConsole.MarkupLine(string.Empty);
        AnsiConsole.MarkupLine("Per-handler invocation spans are available via OTLP when");
        AnsiConsole.MarkupLine("[dim]VAIS_DIAG_SPAN_BUFFER=true[/] is set (use [dim]vais diagnose spans[/]).");
        return Task.FromResult(ProblemDetailsParser.ExitSuccess);
    }
}
