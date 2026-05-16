// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.ComponentModel;
using System.Globalization;
using Spectre.Console;
using Spectre.Console.Cli;
using Vais.Agents.Control.Http;
using Vais.Agents.Eval;

namespace Vais.Agents.Cli.Commands;

/// <summary>Start a new eval run for a named suite.</summary>
internal sealed class EvalRunCommand : AsyncCommand<EvalRunCommand.Settings>
{
    /// <summary>Settings for <see cref="EvalRunCommand"/>.</summary>
    public sealed class Settings : CommandSettings
    {
        /// <summary>Suite name.</summary>
        [Description("Name of the eval suite to run.")]
        [CommandArgument(0, "<suite-name>")]
        public required string SuiteName { get; init; }

        /// <summary>Override the active context.</summary>
        [Description("Override the active context from ~/.vais/config.yaml.")]
        [CommandOption("--context")]
        public string? Context { get; init; }

        /// <summary>Bearer token override.</summary>
        [Description("Bearer token override (wins over VAIS_TOKEN + config).")]
        [CommandOption("--token")]
        public string? Token { get; init; }
    }

    /// <inheritdoc/>
    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var config = VaisConfigFile.LoadOrDefault();
        var client = ClientFactory.Create(config, settings.Context, settings.Token);

        try
        {
            var response = await client.StartEvalRunAsync(settings.SuiteName, cancellationToken);
            AnsiConsole.MarkupLine($"[green]started[/] evalRunId=[bold]{response.EvalRunId}[/]");
            return ProblemDetailsParser.ExitSuccess;
        }
        catch (AgentControlPlaneException ex)
        {
            return ProblemDetailsParser.HandleAndExitCode(ex, AnsiConsole.Console);
        }
    }
}

/// <summary>Fetch and display the results of a completed or in-progress eval run.</summary>
internal sealed class EvalResultsCommand : AsyncCommand<EvalResultsCommand.Settings>
{
    /// <summary>Settings for <see cref="EvalResultsCommand"/>.</summary>
    public sealed class Settings : CommandSettings
    {
        /// <summary>Eval run id.</summary>
        [Description("Eval run id returned by 'vais eval run'.")]
        [CommandArgument(0, "<evalRunId>")]
        public required string EvalRunId { get; init; }

        /// <summary>Output format.</summary>
        [Description("Output format: table | json. Default: table.")]
        [CommandOption("-o|--output")]
        public string? Output { get; init; }

        /// <summary>Override the active context.</summary>
        [Description("Override the active context from ~/.vais/config.yaml.")]
        [CommandOption("--context")]
        public string? Context { get; init; }

        /// <summary>Bearer token override.</summary>
        [Description("Bearer token override (wins over VAIS_TOKEN + config).")]
        [CommandOption("--token")]
        public string? Token { get; init; }
    }

    /// <inheritdoc/>
    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var config = VaisConfigFile.LoadOrDefault();
        var client = ClientFactory.Create(config, settings.Context, settings.Token);

        try
        {
            var detail = await client.GetEvalRunAsync(settings.EvalRunId, cancellationToken);
            if (detail is null)
            {
                AnsiConsole.MarkupLine($"[red]error[/] eval run '{settings.EvalRunId}' not found");
                return ProblemDetailsParser.ExitApiError;
            }

            var format = OutputFormatter.Parse(settings.Output, OutputFormat.Table);
            if (format == OutputFormat.Json)
            {
                OutputFormatter.WriteJson(detail, AnsiConsole.Console);
            }
            else
            {
                RenderDetail(detail);
            }

            return ProblemDetailsParser.ExitSuccess;
        }
        catch (AgentControlPlaneException ex)
        {
            return ProblemDetailsParser.HandleAndExitCode(ex, AnsiConsole.Console);
        }
    }

    private static void RenderDetail(EvalRunDetail detail)
    {
        var s = detail.Summary;
        AnsiConsole.MarkupLine($"[bold]{s.SuiteName}[/] v{s.SuiteVersion} — run [dim]{s.EvalRunId}[/]");
        AnsiConsole.MarkupLine($"Status: [bold]{s.Status}[/]  Pass: [green]{s.PassedCases}[/]  Fail: [red]{s.FailedCases}[/]  Total: {s.TotalCases}");

        if (detail.Cases.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey](no cases)[/]");
            return;
        }

        var table = new Table()
            .AddColumn("CASE")
            .AddColumn("STATUS")
            .AddColumn("ASSERTIONS");

        foreach (var c in detail.Cases)
        {
            var statusMarkup = c.Status == EvalCaseStatus.Pass ? "[green]pass[/]"
                : c.Status == EvalCaseStatus.Error ? "[red]error[/]"
                : "[yellow]fail[/]";

            var assertions = c.AssertionResults.Count == 0
                ? "-"
                : string.Join(", ", c.AssertionResults.Select(a =>
                    a.Status == EvalAssertionStatus.Pass ? $"[green]{Markup.Escape(a.Kind)}✓[/]"
                    : a.Status == EvalAssertionStatus.Error ? $"[red]{Markup.Escape(a.Kind)}![/]"
                    : $"[yellow]{Markup.Escape(a.Kind)}✗[/]"));

            table.AddRow(Markup.Escape(c.CaseId), statusMarkup, assertions);
        }

        AnsiConsole.Write(table);
    }
}

/// <summary>List recent eval runs.</summary>
internal sealed class EvalListCommand : AsyncCommand<EvalListCommand.Settings>
{
    /// <summary>Settings for <see cref="EvalListCommand"/>.</summary>
    public sealed class Settings : CommandSettings
    {
        /// <summary>Filter by suite name.</summary>
        [Description("Filter results by suite name.")]
        [CommandOption("--suite")]
        public string? Suite { get; init; }

        /// <summary>Maximum runs to return.</summary>
        [Description("Maximum number of runs to return. Default: 50.")]
        [CommandOption("--limit")]
        public int? Limit { get; init; }

        /// <summary>Output format.</summary>
        [Description("Output format: table | json. Default: table.")]
        [CommandOption("-o|--output")]
        public string? Output { get; init; }

        /// <summary>Override the active context.</summary>
        [Description("Override the active context from ~/.vais/config.yaml.")]
        [CommandOption("--context")]
        public string? Context { get; init; }

        /// <summary>Bearer token override.</summary>
        [Description("Bearer token override (wins over VAIS_TOKEN + config).")]
        [CommandOption("--token")]
        public string? Token { get; init; }
    }

    /// <inheritdoc/>
    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var config = VaisConfigFile.LoadOrDefault();
        var client = ClientFactory.Create(config, settings.Context, settings.Token);

        try
        {
            var response = await client.ListEvalRunsAsync(settings.Suite, settings.Limit ?? 50, cancellationToken);

            var format = OutputFormatter.Parse(settings.Output, OutputFormat.Table);
            if (format == OutputFormat.Json)
            {
                OutputFormatter.WriteJson(response, AnsiConsole.Console);
            }
            else
            {
                RenderTable(response.Items);
            }

            return ProblemDetailsParser.ExitSuccess;
        }
        catch (AgentControlPlaneException ex)
        {
            return ProblemDetailsParser.HandleAndExitCode(ex, AnsiConsole.Console);
        }
    }

    private static void RenderTable(IReadOnlyList<EvalRunSummary> runs)
    {
        if (runs.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey](no eval runs)[/]");
            return;
        }

        var table = new Table()
            .AddColumn("RUN ID")
            .AddColumn("SUITE")
            .AddColumn("STATUS")
            .AddColumn("PASS")
            .AddColumn("FAIL")
            .AddColumn("STARTED");

        foreach (var r in runs)
        {
            var statusMarkup = r.Status switch
            {
                EvalRunStatus.Completed => "[green]completed[/]",
                EvalRunStatus.Failed => "[red]failed[/]",
                EvalRunStatus.Cancelled => "[yellow]cancelled[/]",
                EvalRunStatus.Running => "[blue]running[/]",
                _ => r.Status.ToString(),
            };
            table.AddRow(
                Markup.Escape(r.EvalRunId),
                Markup.Escape(r.SuiteName),
                statusMarkup,
                r.PassedCases.ToString(CultureInfo.InvariantCulture),
                r.FailedCases.ToString(CultureInfo.InvariantCulture),
                r.StartedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        }

        AnsiConsole.Write(table);
    }
}

/// <summary>Cancel an in-progress eval run.</summary>
internal sealed class EvalCancelCommand : AsyncCommand<EvalCancelCommand.Settings>
{
    /// <summary>Settings for <see cref="EvalCancelCommand"/>.</summary>
    public sealed class Settings : CommandSettings
    {
        /// <summary>Eval run id to cancel.</summary>
        [Description("Eval run id to cancel.")]
        [CommandArgument(0, "<evalRunId>")]
        public required string EvalRunId { get; init; }

        /// <summary>Override the active context.</summary>
        [Description("Override the active context from ~/.vais/config.yaml.")]
        [CommandOption("--context")]
        public string? Context { get; init; }

        /// <summary>Bearer token override.</summary>
        [Description("Bearer token override (wins over VAIS_TOKEN + config).")]
        [CommandOption("--token")]
        public string? Token { get; init; }
    }

    /// <inheritdoc/>
    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var config = VaisConfigFile.LoadOrDefault();
        var client = ClientFactory.Create(config, settings.Context, settings.Token);

        try
        {
            await client.CancelEvalRunAsync(settings.EvalRunId, cancellationToken);
            AnsiConsole.MarkupLine($"{Markup.Escape(settings.EvalRunId)} [yellow]cancel requested[/]");
            return ProblemDetailsParser.ExitSuccess;
        }
        catch (AgentControlPlaneException ex)
        {
            return ProblemDetailsParser.HandleAndExitCode(ex, AnsiConsole.Console);
        }
    }
}
