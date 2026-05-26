// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Vais.Agents.Control.Http;

namespace Vais.Agents.Cli.Commands;

/// <summary>
/// <c>vais recipes propose</c> — run the induction pipeline on the runtime now and persist
/// any new proposals. Returns the proposals just emitted. Idempotent: re-running over the
/// same corpus produces the same proposals (and pre-existing human decisions are preserved).
/// </summary>
internal sealed class RecipesProposeCommand : AsyncCommand<RecipesProposeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Restrict induction to one coordinator agent's traces.")]
        [CommandOption("--agent")]
        public string? Agent { get; init; }

        [Description("Restrict induction to one run id.")]
        [CommandOption("--run")]
        public string? Run { get; init; }

        [Description("Restrict induction to one tool / verb.")]
        [CommandOption("--concept")]
        public string? Concept { get; init; }

        [Description("Restrict induction to one transport: north | south.")]
        [CommandOption("--transport")]
        public string? Transport { get; init; }

        [Description("Restrict induction to events at or after this ISO 8601 timestamp.")]
        [CommandOption("--since")]
        public string? Since { get; init; }

        [Description("Restrict induction to events before this ISO 8601 timestamp.")]
        [CommandOption("--until")]
        public string? Until { get; init; }

        [Description("Output format: table | json. Default: table.")]
        [CommandOption("-o|--output")]
        public string? Output { get; init; }

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

        DateTimeOffset? since = DateTimeOffset.TryParse(settings.Since, out var s) ? s : null;
        DateTimeOffset? until = DateTimeOffset.TryParse(settings.Until, out var u) ? u : null;

        try
        {
            var proposals = await client.ProposeRecipesAsync(
                agent: settings.Agent,
                run: settings.Run,
                concept: settings.Concept,
                transport: settings.Transport,
                since: since,
                until: until,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var format = OutputFormatter.Parse(settings.Output, OutputFormat.Table);
            if (format == OutputFormat.Json)
            {
                OutputFormatter.WriteJson(proposals, AnsiConsole.Console);
                return ProblemDetailsParser.ExitSuccess;
            }

            if (proposals.Count == 0)
            {
                AnsiConsole.MarkupLine("[grey](no new proposals — the corpus didn't yield any patterns above the threshold)[/]");
                return ProblemDetailsParser.ExitSuccess;
            }

            var table = new Table()
                .AddColumn("ID")
                .AddColumn("CONCEPT")
                .AddColumn("RISK")
                .AddColumn("SUPPORT")
                .AddColumn("CONFIDENCE")
                .AddColumn("BODY");

            foreach (var p in proposals)
            {
                table.AddRow(
                    p.ProposalId.Length > 12 ? p.ProposalId[..12] : p.ProposalId,
                    Markup.Escape(p.Concept),
                    RecipesListCommand.RiskMarkup(p.RiskLevel),
                    p.Support.ToString(),
                    $"{p.Confidence:P0}",
                    Markup.Escape(p.Body.Length > 60 ? p.Body[..60] + "…" : p.Body));
            }

            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine($"[grey]{proposals.Count} proposal(s) emitted. Review with 'vais recipes list --status Pending'.[/]");
            return ProblemDetailsParser.ExitSuccess;
        }
        catch (AgentControlPlaneException ex)
        {
            return ProblemDetailsParser.HandleAndExitCode(ex, AnsiConsole.Console);
        }
    }
}
