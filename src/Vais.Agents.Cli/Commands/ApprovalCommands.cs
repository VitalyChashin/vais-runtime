// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Vais.Agents.Control;
using Vais.Agents.Control.Http;

namespace Vais.Agents.Cli.Commands;

/// <summary>Shared connection options for the approval commands.</summary>
internal abstract class ApprovalSettingsBase : CommandSettings
{
    /// <summary>Override the active context.</summary>
    [Description("Override the active context from ~/.vais/config.yaml.")]
    [CommandOption("--context")]
    public string? Context { get; init; }

    /// <summary>Bearer token override.</summary>
    [Description("Bearer token override (wins over VAIS_TOKEN + config).")]
    [CommandOption("--token")]
    public string? Token { get; init; }
}

/// <summary>List high-risk mutation approval requests.</summary>
internal sealed class ApprovalListCommand : AsyncCommand<ApprovalListCommand.Settings>
{
    /// <summary>Settings for <see cref="ApprovalListCommand"/>.</summary>
    public sealed class Settings : ApprovalSettingsBase
    {
        /// <summary>Optional status filter.</summary>
        [Description("Filter by status: pending | approved | rejected.")]
        [CommandOption("-s|--status")]
        public string? Status { get; init; }
    }

    /// <inheritdoc/>
    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var config = VaisConfigFile.LoadOrDefault();
        var client = ClientFactory.Create(config, settings.Context, settings.Token);
        try
        {
            var items = await client.ListApprovalsAsync(settings.Status, cancellationToken);
            if (items.Count == 0)
            {
                AnsiConsole.MarkupLine("[grey]no approval requests[/]");
                return ProblemDetailsParser.ExitSuccess;
            }

            var table = new Table();
            table.AddColumn("ID");
            table.AddColumn("Kind");
            table.AddColumn("Name");
            table.AddColumn("Status");
            table.AddColumn("Requested By");
            foreach (var a in items)
            {
                table.AddRow(
                    Markup.Escape(a.RequestId),
                    Markup.Escape(a.Kind),
                    Markup.Escape(a.Name),
                    StatusMarkup(a.Status),
                    Markup.Escape(a.RequestedBy));
            }
            AnsiConsole.Write(table);
            return ProblemDetailsParser.ExitSuccess;
        }
        catch (AgentControlPlaneException ex)
        {
            return ProblemDetailsParser.HandleAndExitCode(ex, AnsiConsole.Console);
        }
    }

    private static string StatusMarkup(ApprovalStatus s) => s switch
    {
        ApprovalStatus.Pending => "[yellow]pending[/]",
        ApprovalStatus.Approved => "[green]approved[/]",
        ApprovalStatus.Rejected => "[red]rejected[/]",
        _ => s.ToString(),
    };
}

/// <summary>Approve a pending high-risk mutation.</summary>
internal sealed class ApprovalApproveCommand : AsyncCommand<ApprovalApproveCommand.Settings>
{
    /// <summary>Settings for <see cref="ApprovalApproveCommand"/>.</summary>
    public sealed class Settings : ApprovalSettingsBase
    {
        /// <summary>Approval request id.</summary>
        [Description("Approval request id (from 'vais approvals list').")]
        [CommandArgument(0, "<requestId>")]
        public required string RequestId { get; init; }
    }

    /// <inheritdoc/>
    protected override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
        => ApprovalDecide.RunAsync(settings.RequestId, approve: true, settings.Context, settings.Token, cancellationToken);
}

/// <summary>Reject a pending high-risk mutation.</summary>
internal sealed class ApprovalRejectCommand : AsyncCommand<ApprovalRejectCommand.Settings>
{
    /// <summary>Settings for <see cref="ApprovalRejectCommand"/>.</summary>
    public sealed class Settings : ApprovalSettingsBase
    {
        /// <summary>Approval request id.</summary>
        [Description("Approval request id (from 'vais approvals list').")]
        [CommandArgument(0, "<requestId>")]
        public required string RequestId { get; init; }
    }

    /// <inheritdoc/>
    protected override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
        => ApprovalDecide.RunAsync(settings.RequestId, approve: false, settings.Context, settings.Token, cancellationToken);
}

/// <summary>Shared approve/reject logic.</summary>
internal static class ApprovalDecide
{
    public static async Task<int> RunAsync(string requestId, bool approve, string? contextName, string? token, CancellationToken ct)
    {
        var config = VaisConfigFile.LoadOrDefault();
        var client = ClientFactory.Create(config, contextName, token);
        try
        {
            var result = approve
                ? await client.ApproveAsync(requestId, ct)
                : await client.RejectAsync(requestId, ct);

            if (result is null)
            {
                AnsiConsole.MarkupLine($"[red]error[/] approval '{Markup.Escape(requestId)}' not found or already decided");
                return ProblemDetailsParser.ExitApiError;
            }

            var verb = approve ? "[green]approved[/]" : "[red]rejected[/]";
            AnsiConsole.MarkupLine($"{Markup.Escape(result.RequestId)} {verb} ({Markup.Escape(result.Kind)}/{Markup.Escape(result.Name)})");
            return ProblemDetailsParser.ExitSuccess;
        }
        catch (AgentControlPlaneException ex)
        {
            return ProblemDetailsParser.HandleAndExitCode(ex, AnsiConsole.Console);
        }
    }
}
