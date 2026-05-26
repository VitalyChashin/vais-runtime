// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Vais.Agents.Control.Http;

namespace Vais.Agents.Cli.Commands;

/// <summary>
/// <c>vais recipes show &lt;proposalId&gt;</c> — fetch a single induced recipe proposal by id.
/// </summary>
internal sealed class RecipesShowCommand : AsyncCommand<RecipesShowCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Proposal id (full or unambiguous prefix).")]
        [CommandArgument(0, "<proposalId>")]
        public string ProposalId { get; init; } = string.Empty;

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
            var proposal = await client.GetRecipeAsync(settings.ProposalId, cancellationToken).ConfigureAwait(false);
            if (proposal is null)
            {
                AnsiConsole.MarkupLine($"[red]Proposal '{Markup.Escape(settings.ProposalId)}' not found.[/]");
                return 1;
            }

            var format = OutputFormatter.Parse(settings.Output, OutputFormat.Table);
            if (format == OutputFormat.Json)
            {
                OutputFormatter.WriteJson(proposal, AnsiConsole.Console);
                return ProblemDetailsParser.ExitSuccess;
            }

            var grid = new Grid().AddColumn().AddColumn();
            grid.AddRow("[bold]ID[/]", proposal.ProposalId);
            grid.AddRow("[bold]Concept[/]", Markup.Escape(proposal.Concept));
            grid.AddRow("[bold]Kind[/]", proposal.Kind.ToString());
            grid.AddRow("[bold]Status[/]", RecipesListCommand.StatusMarkup(proposal.Status));
            grid.AddRow("[bold]Risk[/]", RecipesListCommand.RiskMarkup(proposal.RiskLevel));
            grid.AddRow("[bold]Support[/]", proposal.Support.ToString());
            grid.AddRow("[bold]Confidence[/]", $"{proposal.Confidence:P1}");
            grid.AddRow("[bold]Body[/]", Markup.Escape(proposal.Body));
            if (proposal.Name is { Length: > 0 } name)
                grid.AddRow("[bold]Name[/]", Markup.Escape(name));
            grid.AddRow("[bold]Created[/]", proposal.CreatedAt.ToString("u"));
            if (proposal.ReviewedAt.HasValue)
                grid.AddRow("[bold]Reviewed[/]", $"{proposal.ReviewedAt.Value:u} by {Markup.Escape(proposal.ReviewerId ?? "?")}");
            grid.AddRow("[bold]Source traces[/]", string.Join(", ", proposal.SourceTraceIds.Take(8)) + (proposal.SourceTraceIds.Count > 8 ? "…" : ""));

            AnsiConsole.Write(grid);
            return ProblemDetailsParser.ExitSuccess;
        }
        catch (AgentControlPlaneException ex)
        {
            return ProblemDetailsParser.HandleAndExitCode(ex, AnsiConsole.Console);
        }
    }
}
