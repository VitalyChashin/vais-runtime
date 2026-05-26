// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Vais.Agents.Control.Http;

namespace Vais.Agents.Cli.Commands;

/// <summary>
/// <c>vais recipes approve|reject &lt;proposalId&gt;</c> — flip a pending proposal. For
/// high-risk proposals this may surface 202 Accepted with an approval request id; the
/// operator then approves it via <c>vais approvals approve &lt;requestId&gt;</c> and re-runs
/// this command.
/// </summary>
internal sealed class RecipesDecideCommand : AsyncCommand<RecipesDecideCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Proposal id to decide.")]
        [CommandArgument(0, "<proposalId>")]
        public string ProposalId { get; init; } = string.Empty;

        [Description("Reviewer identity to record on the proposal. Defaults to $USER or 'anonymous'.")]
        [CommandOption("--by")]
        public string? DecidedBy { get; init; }

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
        // Approve vs reject is differentiated by the verb registered at the branch level
        // (config.AddCommand<...>("approve").WithData(true) / WithData(false)).
        var approve = context.Data is true;

        var config = VaisConfigFile.LoadOrDefault();
        var client = ClientFactory.Create(config, settings.Context, settings.Token);
        var decidedBy = settings.DecidedBy
            ?? Environment.GetEnvironmentVariable("USER")
            ?? Environment.GetEnvironmentVariable("USERNAME")
            ?? "anonymous";

        try
        {
            var result = await client.DecideRecipeAsync(settings.ProposalId, approve, decidedBy, cancellationToken).ConfigureAwait(false);
            if (result is null)
            {
                AnsiConsole.MarkupLine($"[red]Proposal '{Markup.Escape(settings.ProposalId)}' not found.[/]");
                return 1;
            }

            var verb = approve ? "approved" : "rejected";
            AnsiConsole.MarkupLine($"[green]Proposal {Markup.Escape(result.ProposalId)} {verb}[/] (by {Markup.Escape(decidedBy)}).");
            AnsiConsole.MarkupLine($"  Status: {RecipesListCommand.StatusMarkup(result.Status)}");
            return ProblemDetailsParser.ExitSuccess;
        }
        catch (AgentControlPlaneException ex)
        {
            return ProblemDetailsParser.HandleAndExitCode(ex, AnsiConsole.Console);
        }
    }
}
