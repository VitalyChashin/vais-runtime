// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Vais.Agents.Control;
using Vais.Agents.Control.Http;
using Vais.Agents.Control.Manifests;

namespace Vais.Agents.Cli.Commands;

/// <summary>
/// Reads agent / graph manifests from a file (YAML or JSON) and creates or
/// updates them via the HTTP control plane. Mirrors <c>kubectl apply -f</c> —
/// server-side-apply via create-or-update dispatch. Handles mixed-kind files
/// (agents + graphs in one <c>---</c>-separated YAML) via
/// <see cref="JsonAgentGraphManifestLoader.LoadAllResourcesFromStringAsync"/> /
/// <see cref="YamlAgentGraphManifestLoader.LoadAllResourcesFromStringAsync"/>.
/// </summary>
internal sealed class ApplyCommand : AsyncCommand<ApplyCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Path to the manifest file (.yaml / .yml / .json). Use '-' to read from stdin as YAML.")]
        [CommandOption("-f|--file")]
        public required string File { get; init; }

        [Description("Idempotency-Key to attach to the control-plane request (v0.11 wire-dedup). Random when omitted.")]
        [CommandOption("--idempotency-key")]
        public string? IdempotencyKey { get; init; }

        [Description("Override the active context from ~/.vais/config.yaml.")]
        [CommandOption("--context")]
        public string? Context { get; init; }

        [Description("Bearer token override (wins over VAIS_TOKEN + config).")]
        [CommandOption("--token")]
        public string? Token { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        string content;
        string filenameHint;
        if (settings.File == "-")
        {
            content = await Console.In.ReadToEndAsync(cancellationToken);
            filenameHint = "<stdin>.yaml";
        }
        else
        {
            if (!System.IO.File.Exists(settings.File))
            {
                AnsiConsole.MarkupLine($"[red]error[/] file not found: {settings.File}");
                return ProblemDetailsParser.ExitUsageError;
            }
            content = await System.IO.File.ReadAllTextAsync(settings.File, cancellationToken);
            filenameHint = settings.File;
        }

        IReadOnlyList<ManifestResource> resources;
        try
        {
            resources = IsJson(filenameHint)
                ? await new JsonAgentGraphManifestLoader().LoadAllResourcesFromStringAsync(content, cancellationToken)
                : await new YamlAgentGraphManifestLoader().LoadAllResourcesFromStringAsync(content, cancellationToken);
        }
        catch (AgentManifestValidationException ex)
        {
            AnsiConsole.MarkupLine($"[red]error[/] manifest validation failed:");
            foreach (var err in ex.Errors)
            {
                AnsiConsole.MarkupLine($"  - {err}");
            }
            return ProblemDetailsParser.ExitUsageError;
        }

        if (resources.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]warning[/] no manifests parsed from input");
            return ProblemDetailsParser.ExitUsageError;
        }

        var config = VaisConfigFile.LoadOrDefault();
        var client = ClientFactory.Create(config, settings.Context, settings.Token);

        var anyError = false;
        foreach (var resource in resources)
        {
            var idempotencyKey = settings.IdempotencyKey ?? Guid.NewGuid().ToString("N");
            try
            {
                switch (resource)
                {
                    case ManifestResource.AgentCase agentCase:
                        await ApplyAgentAsync(client, agentCase.Manifest, idempotencyKey, cancellationToken);
                        break;
                    case ManifestResource.AgentGraphCase graphCase:
                        await ApplyGraphAsync(client, graphCase.Graph, idempotencyKey, cancellationToken);
                        break;
                    default:
                        AnsiConsole.MarkupLine($"[yellow]warning[/] unknown resource kind: {resource.GetType().Name}");
                        break;
                }
            }
            catch (AgentControlPlaneException ex)
            {
                anyError = true;
                var code = ProblemDetailsParser.HandleAndExitCode(ex, AnsiConsole.Console);
                if (code != ProblemDetailsParser.ExitApiError)
                {
                    return code;
                }
            }
        }
        return anyError ? ProblemDetailsParser.ExitApiError : ProblemDetailsParser.ExitSuccess;
    }

    private static async Task ApplyAgentAsync(IAgentControlPlaneClient client, AgentManifest manifest, string idempotencyKey, CancellationToken ct)
    {
        try
        {
            var handle = await client.CreateAsync(manifest, idempotencyKey, ct);
            AnsiConsole.MarkupLine($"{handle.AgentId} [green]created[/] (version {handle.Version})");
        }
        catch (AgentControlPlaneException ex) when (ProblemDetailsParser.IsConflict(ex))
        {
            var updated = await client.UpdateAsync(manifest.Id, manifest, manifest.Version, idempotencyKey, ct);
            AnsiConsole.MarkupLine($"{updated.AgentId} [blue]updated[/] (version {updated.Version})");
        }
    }

    private static async Task ApplyGraphAsync(IAgentControlPlaneClient client, AgentGraphManifest graph, string idempotencyKey, CancellationToken ct)
    {
        try
        {
            var handle = await client.CreateGraphAsync(graph, idempotencyKey, ct);
            AnsiConsole.MarkupLine($"{handle.GraphId} [green]created[/] (graph, version {handle.Version})");
        }
        catch (AgentControlPlaneException ex) when (ProblemDetailsParser.IsConflict(ex))
        {
            var updated = await client.UpdateGraphAsync(graph.Id, graph, graph.Version, idempotencyKey, ct);
            AnsiConsole.MarkupLine($"{updated.GraphId} [blue]updated[/] (graph, version {updated.Version})");
        }
    }

    private static bool IsJson(string pathOrHint)
        => pathOrHint.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
}
