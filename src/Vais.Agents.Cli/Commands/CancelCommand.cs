// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Vais.Agents.Control.Http;

namespace Vais.Agents.Cli.Commands;

/// <summary>
/// Non-destructive — maps to <c>IAgentControlPlaneClient.CancelAsync</c>.
/// Cancels in-flight work on the agent; manifest + state remain. No
/// confirm prompt.
/// </summary>
internal sealed class CancelCommand : AsyncCommand<CancelCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Agent id to cancel.")]
        [CommandArgument(0, "<id>")]
        public required string AgentId { get; init; }

        [Description("Version to cancel (omit for the latest version).")]
        [CommandOption("--version")]
        public string? Version { get; init; }

        [Description("Idempotency-Key on the outbound Cancel call.")]
        [CommandOption("--idempotency-key")]
        public string? IdempotencyKey { get; init; }

        [Description("Override the active context.")]
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
            var idempotencyKey = settings.IdempotencyKey ?? Guid.NewGuid().ToString("N");
            await client.CancelAsync(settings.AgentId, settings.Version, idempotencyKey, cancellationToken);
            AnsiConsole.MarkupLine($"{settings.AgentId} [yellow]cancelled[/]");
            return ProblemDetailsParser.ExitSuccess;
        }
        catch (AgentControlPlaneException ex)
        {
            return ProblemDetailsParser.HandleAndExitCode(ex, AnsiConsole.Console);
        }
    }
}
