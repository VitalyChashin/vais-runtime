// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Vais.Agents.Control.Http;

namespace Vais.Agents.Cli.Commands;

/// <summary>
/// Destructive — maps to <c>IAgentControlPlaneClient.EvictGraphAsync</c>.
/// Prompts confirm when stdin is a TTY and <c>--force</c> is not set.
/// Parallel to <see cref="DeleteCommand"/> for agents.
/// </summary>
internal sealed class DeleteGraphCommand : AsyncCommand<DeleteGraphCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Graph id to delete.")]
        [CommandArgument(0, "<id>")]
        public required string GraphId { get; init; }

        [Description("Version to evict (omit for all versions).")]
        [CommandOption("--version")]
        public string? Version { get; init; }

        [Description("Skip the interactive confirm prompt. Auto-set when stdin isn't a TTY.")]
        [CommandOption("--force")]
        public bool Force { get; init; }

        [Description("Idempotency-Key on the outbound Evict call.")]
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
        if (!settings.Force && AnsiConsole.Profile.Capabilities.Interactive)
        {
            var versionSuffix = string.IsNullOrWhiteSpace(settings.Version) ? string.Empty : $" (version {settings.Version})";
            if (!AnsiConsole.Confirm($"Delete graph [bold]'{settings.GraphId}'[/]{versionSuffix}?", defaultValue: false))
            {
                AnsiConsole.MarkupLine("[yellow]cancelled[/]");
                return ProblemDetailsParser.ExitSuccess;
            }
        }

        var config = VaisConfigFile.LoadOrDefault();
        var client = ClientFactory.Create(config, settings.Context, settings.Token);

        try
        {
            var idempotencyKey = settings.IdempotencyKey ?? Guid.NewGuid().ToString("N");
            await client.EvictGraphAsync(settings.GraphId, settings.Version, idempotencyKey, cancellationToken);
            AnsiConsole.MarkupLine($"{settings.GraphId} [red]deleted[/]");
            return ProblemDetailsParser.ExitSuccess;
        }
        catch (AgentControlPlaneException ex)
        {
            return ProblemDetailsParser.HandleAndExitCode(ex, AnsiConsole.Console);
        }
    }
}
