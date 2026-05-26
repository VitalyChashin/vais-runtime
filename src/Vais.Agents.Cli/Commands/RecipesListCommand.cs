// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Vais.Agents.Control.Http;

namespace Vais.Agents.Cli.Commands;

/// <summary>
/// <c>vais recipes list</c> — list induced recipe proposals (Plan D) from the runtime
/// control plane. Newest-first; filters AND-combined. The output is the triage surface for
/// the human reviewer.
/// </summary>
internal sealed class RecipesListCommand : AsyncCommand<RecipesListCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Filter to one anchor concept (tool/verb name).")]
        [CommandOption("--concept")]
        public string? Concept { get; init; }

        [Description("Filter to one proposal kind: WorkflowRecipe | TagSuggestion | DescriptionRewrite.")]
        [CommandOption("--kind")]
        public string? Kind { get; init; }

        [Description("Filter to one status: Pending | Approved | Rejected | Superseded.")]
        [CommandOption("--status")]
        public string? Status { get; init; }

        [Description("Filter to one risk level: Low | Medium | High.")]
        [CommandOption("--risk")]
        public string? Risk { get; init; }

        [Description("Maximum number of proposals to return (default 50, max 500).")]
        [CommandOption("--limit")]
        public int Limit { get; init; } = 50;

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

        try
        {
            var proposals = await client.ListRecipesAsync(
                concept: settings.Concept,
                kind: settings.Kind,
                status: settings.Status,
                risk: settings.Risk,
                limit: settings.Limit,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var format = OutputFormatter.Parse(settings.Output, OutputFormat.Table);
            if (format == OutputFormat.Json)
            {
                OutputFormatter.WriteJson(proposals, AnsiConsole.Console);
                return ProblemDetailsParser.ExitSuccess;
            }

            if (proposals.Count == 0)
            {
                AnsiConsole.MarkupLine("[grey](no proposals)[/]");
                return ProblemDetailsParser.ExitSuccess;
            }

            var table = new Table()
                .AddColumn("ID")
                .AddColumn("CONCEPT")
                .AddColumn("KIND")
                .AddColumn("STATUS")
                .AddColumn("RISK")
                .AddColumn("SUPPORT")
                .AddColumn("CONFIDENCE")
                .AddColumn("BODY");

            foreach (var p in proposals)
            {
                table.AddRow(
                    p.ProposalId.Length > 12 ? p.ProposalId[..12] : p.ProposalId,
                    Markup.Escape(p.Concept),
                    p.Kind.ToString(),
                    StatusMarkup(p.Status),
                    RiskMarkup(p.RiskLevel),
                    p.Support.ToString(),
                    $"{p.Confidence:P0}",
                    Markup.Escape(p.Body.Length > 60 ? p.Body[..60] + "…" : p.Body));
            }

            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine($"[grey]{proposals.Count} proposal(s); newest first.[/]");
            return ProblemDetailsParser.ExitSuccess;
        }
        catch (AgentControlPlaneException ex)
        {
            return ProblemDetailsParser.HandleAndExitCode(ex, AnsiConsole.Console);
        }
    }

    internal static string StatusMarkup(RecipeProposalStatus s) => s switch
    {
        RecipeProposalStatus.Pending => "[yellow]pending[/]",
        RecipeProposalStatus.Approved => "[green]approved[/]",
        RecipeProposalStatus.Rejected => "[red]rejected[/]",
        RecipeProposalStatus.Superseded => "[grey]superseded[/]",
        _ => s.ToString(),
    };

    internal static string RiskMarkup(RecipeProposalRiskLevel r) => r switch
    {
        RecipeProposalRiskLevel.Low => "[grey]low[/]",
        RecipeProposalRiskLevel.Medium => "[yellow]medium[/]",
        RecipeProposalRiskLevel.High => "[red]high[/]",
        _ => r.ToString(),
    };
}
