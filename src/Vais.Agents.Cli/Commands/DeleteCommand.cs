// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Vais.Agents.Control.Http;

namespace Vais.Agents.Cli.Commands;

/// <summary>
/// Destructive delete. Accepts either a plain agent id (backward-compat) or a
/// <c>&lt;resource-type&gt;/&lt;id&gt;</c> path for gateway resources:
/// <c>agents/&lt;id&gt;</c>, <c>llm-gateways/&lt;id&gt;</c>,
/// <c>mcp-gateways/&lt;id&gt;</c>, <c>mcp-servers/&lt;id&gt;</c>, <c>eval-suites/&lt;id&gt;</c>.
/// Prompts confirm when stdin is a TTY and <c>--force</c> is not set.
/// </summary>
internal sealed class DeleteCommand : AsyncCommand<DeleteCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Resource to delete: plain agent-id, or <resource-type>/<id> (agents/, llm-gateways/, mcp-gateways/, mcp-servers/).")]
        [CommandArgument(0, "<resource>")]
        public required string Resource { get; init; }

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
        var (resourceKind, resourceId) = ParseResource(settings.Resource);

        if (!settings.Force && AnsiConsole.Profile.Capabilities.Interactive)
        {
            var versionSuffix = string.IsNullOrWhiteSpace(settings.Version) ? string.Empty : $" (version {settings.Version})";
            if (!AnsiConsole.Confirm($"Delete {resourceKind} [bold]'{resourceId}'[/]{versionSuffix}?", defaultValue: false))
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
            switch (resourceKind)
            {
                case "llm-gateways":
                    await client.EvictLlmGatewayConfigAsync(resourceId, settings.Version, cancellationToken);
                    break;
                case "mcp-gateways":
                    await client.EvictMcpGatewayConfigAsync(resourceId, settings.Version, cancellationToken);
                    break;
                case "mcp-servers":
                    await client.EvictMcpServerAsync(resourceId, settings.Version, cancellationToken);
                    break;
                case "eval-suites":
                    await client.EvictEvalSuiteAsync(resourceId, settings.Version, cancellationToken);
                    break;
                default:
                    await client.EvictAsync(resourceId, settings.Version, idempotencyKey, cancellationToken);
                    break;
            }
            AnsiConsole.MarkupLine($"{resourceId} [red]deleted[/]");
            return ProblemDetailsParser.ExitSuccess;
        }
        catch (AgentControlPlaneException ex)
        {
            return ProblemDetailsParser.HandleAndExitCode(ex, AnsiConsole.Console);
        }
    }

    private static (string Kind, string Id) ParseResource(string resource)
    {
        var slash = resource.IndexOf('/', StringComparison.Ordinal);
        if (slash > 0)
        {
            var kind = resource[..slash];
            var id = resource[(slash + 1)..];
            return (kind, id);
        }
        return ("agents", resource);
    }
}
