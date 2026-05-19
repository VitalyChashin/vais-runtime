// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Vais.Agents.Cli.Commands;

/// <summary>
/// Shows per-handler latency metrics (p50/p95) for a loaded extension.
/// Metrics collection requires an OTLP-compatible backend; direct CLI metrics
/// are planned for a future release.
/// </summary>
internal sealed class ExtMetricsCommand : AsyncCommand<ExtMetricsCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Extension id.")]
        [CommandArgument(0, "<name>")]
        public string Name { get; init; } = string.Empty;

        [Description("Override the active context from ~/.vais/config.yaml.")]
        [CommandOption("--context")]
        public string? Context { get; init; }

        [Description("Bearer token override (wins over VAIS_TOKEN + config).")]
        [CommandOption("--token")]
        public string? Token { get; init; }
    }

    protected override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine($"[yellow]Per-handler latency metrics (p50/p95) are not yet exposed via the CLI.[/]");
        AnsiConsole.MarkupLine(string.Empty);
        AnsiConsole.MarkupLine("Handler invocation timing is emitted as OTLP spans when the runtime");
        AnsiConsole.MarkupLine("is configured with an OTLP exporter. Query your observability backend");
        AnsiConsole.MarkupLine($"(Grafana, Jaeger, etc.) filtering on [dim]vais.extension.handler[/] spans.");
        AnsiConsole.MarkupLine(string.Empty);
        AnsiConsole.MarkupLine("In-process span buffer (for dev): [dim]vais diagnose spans[/]");
        return Task.FromResult(ProblemDetailsParser.ExitSuccess);
    }
}
