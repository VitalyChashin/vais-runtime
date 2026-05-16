// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
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

        /// <summary>Stream progress and wait for run completion before returning.</summary>
        [Description("Stream case-completion events and wait until the run finishes.")]
        [CommandOption("-w|--wait")]
        public bool Wait { get; init; }
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

            if (settings.Wait)
                await WaitForCompletionAsync(client, response.EvalRunId, cancellationToken);

            return ProblemDetailsParser.ExitSuccess;
        }
        catch (AgentControlPlaneException ex)
        {
            return ProblemDetailsParser.HandleAndExitCode(ex, AnsiConsole.Console);
        }
    }

    private static async Task WaitForCompletionAsync(
        IAgentControlPlaneClient client, string evalRunId, CancellationToken ct)
    {
        await foreach (var dataJson in client.StreamEvalRunAsync(evalRunId, ct))
        {
            try
            {
                using var doc = JsonDocument.Parse(dataJson);
                var root = doc.RootElement;
                var kind = root.TryGetProperty("progressKind", out var kp) ? kp.GetString() : null;
                var caseId = root.TryGetProperty("caseId", out var cp) ? cp.GetString() : null;
                var statusInt = root.TryGetProperty("caseStatus", out var sp)
                    && sp.ValueKind == JsonValueKind.Number ? sp.GetInt32() : -1;

                if (kind == "case-completed" && caseId is not null)
                {
                    var mark = statusInt switch
                    {
                        (int)EvalCaseStatus.Pass => "[green]pass[/]",
                        (int)EvalCaseStatus.Fail => "[yellow]fail[/]",
                        (int)EvalCaseStatus.Error => "[red]error[/]",
                        _ => "[grey]?[/]",
                    };
                    AnsiConsole.MarkupLine($"  {mark} {Markup.Escape(caseId)}");
                }
                else if (kind == "run-completed")
                {
                    AnsiConsole.MarkupLine("[green]run completed[/]");
                }
            }
            catch (JsonException) { /* skip malformed lines */ }
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
        [Description("Output format: table | json | junit. Default: table.")]
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
                OutputFormatter.WriteJson(detail, AnsiConsole.Console);
            else if (format == OutputFormat.JUnit)
                AnsiConsole.Console.WriteLine(RenderJUnit(detail));
            else
                RenderDetail(detail);

            return ProblemDetailsParser.ExitSuccess;
        }
        catch (AgentControlPlaneException ex)
        {
            return ProblemDetailsParser.HandleAndExitCode(ex, AnsiConsole.Console);
        }
    }

    private static string RenderJUnit(EvalRunDetail detail)
    {
        var s = detail.Summary;
        var totalTime = detail.Cases
            .Where(c => c.CompletedAt.HasValue)
            .Sum(c => (c.CompletedAt!.Value - c.StartedAt).TotalSeconds);

        var testCases = detail.Cases.Select(c =>
        {
            var elapsed = c.CompletedAt.HasValue
                ? (c.CompletedAt.Value - c.StartedAt).TotalSeconds
                    .ToString("F3", CultureInfo.InvariantCulture)
                : "0.000";
            var elem = new XElement("testcase",
                new XAttribute("name", c.CaseId),
                new XAttribute("classname", s.SuiteName),
                new XAttribute("time", elapsed));

            if (c.Status == EvalCaseStatus.Error)
            {
                elem.Add(new XElement("error",
                    new XAttribute("message", "Agent invocation error")));
            }
            else if (c.Status == EvalCaseStatus.Fail)
            {
                var failedAssertions = c.AssertionResults
                    .Where(a => a.Status == EvalAssertionStatus.Fail)
                    .Select(a => $"{a.Kind}: {a.Reason ?? "failed"}");
                elem.Add(new XElement("failure",
                    new XAttribute("message", string.Join("; ", failedAssertions))));
            }
            return elem;
        });

        var suite = new XElement("testsuite",
            new XAttribute("name", s.SuiteName),
            new XAttribute("tests", s.TotalCases),
            new XAttribute("failures", s.FailedCases),
            new XAttribute("errors", detail.Cases.Count(c => c.Status == EvalCaseStatus.Error)),
            new XAttribute("time", totalTime.ToString("F3", CultureInfo.InvariantCulture)),
            testCases);

        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement("testsuites",
                new XAttribute("name", s.SuiteName),
                new XAttribute("tests", s.TotalCases),
                new XAttribute("failures", s.FailedCases),
                new XAttribute("time", totalTime.ToString("F3", CultureInfo.InvariantCulture)),
                suite));

        var sb = new StringBuilder();
        using var writer = new System.IO.StringWriter(sb);
        doc.Save(writer);
        return sb.ToString();
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

/// <summary>Compare two eval runs case-by-case and show assertion deltas.</summary>
internal sealed class EvalDiffCommand : AsyncCommand<EvalDiffCommand.Settings>
{
    /// <summary>Settings for <see cref="EvalDiffCommand"/>.</summary>
    public sealed class Settings : CommandSettings
    {
        /// <summary>Base eval run id.</summary>
        [Description("Base (reference) eval run id.")]
        [CommandArgument(0, "<base-run-id>")]
        public required string BaseRunId { get; init; }

        /// <summary>Candidate eval run id.</summary>
        [Description("Candidate eval run id to compare against base.")]
        [CommandArgument(1, "<candidate-run-id>")]
        public required string CandidateRunId { get; init; }

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
            var diff = await client.GetEvalDiffAsync(settings.BaseRunId, settings.CandidateRunId, cancellationToken);
            if (diff is null)
            {
                AnsiConsole.MarkupLine("[red]error[/] one or both eval runs not found");
                return ProblemDetailsParser.ExitApiError;
            }

            var format = OutputFormatter.Parse(settings.Output, OutputFormat.Table);
            if (format == OutputFormat.Json)
            {
                OutputFormatter.WriteJson(diff, AnsiConsole.Console);
            }
            else
            {
                RenderDiff(diff);
            }

            return ProblemDetailsParser.ExitSuccess;
        }
        catch (AgentControlPlaneException ex)
        {
            return ProblemDetailsParser.HandleAndExitCode(ex, AnsiConsole.Console);
        }
    }

    private static void RenderDiff(EvalDiffResponse diff)
    {
        AnsiConsole.MarkupLine($"[dim]base:[/]      {Markup.Escape(diff.BaseRunId)}");
        AnsiConsole.MarkupLine($"[dim]candidate:[/] {Markup.Escape(diff.CandidateRunId)}");

        if (diff.Cases.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey](no cases in diff)[/]");
            return;
        }

        var table = new Table()
            .AddColumn("CASE")
            .AddColumn("BASE")
            .AddColumn("CANDIDATE")
            .AddColumn("CHANGED");

        foreach (var c in diff.Cases)
        {
            var baseLabel = StatusLabel(c.BaseStatus);
            var candidateLabel = StatusLabel(c.CandidateStatus);
            var changed = c.BaseStatus != c.CandidateStatus ? "[yellow]yes[/]" : "[grey]no[/]";
            table.AddRow(Markup.Escape(c.CaseId), baseLabel, candidateLabel, changed);
        }

        AnsiConsole.Write(table);
    }

    private static string StatusLabel(EvalCaseStatus? s) => s switch
    {
        EvalCaseStatus.Pass => "[green]pass[/]",
        EvalCaseStatus.Fail => "[yellow]fail[/]",
        EvalCaseStatus.Error => "[red]error[/]",
        null => "[grey]-[/]",
        _ => s.ToString()!,
    };
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
