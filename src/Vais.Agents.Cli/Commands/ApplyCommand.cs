// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Vais.Agents.Control.Http;
using Vais.Agents.Control.Manifests;

namespace Vais.Agents.Cli.Commands;

/// <summary>
/// Reads an agent manifest from a file (YAML or JSON) and creates or
/// updates the agent via the HTTP control plane. Mirrors
/// <c>kubectl apply -f</c> — server-side-apply via create-or-update
/// dispatch: first <c>CreateAsync</c>; on 409 conflict falls back to
/// <c>UpdateAsync</c>.
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

        IReadOnlyList<AgentManifest> manifests;
        try
        {
            manifests = IsJson(filenameHint)
                ? await new JsonAgentManifestLoader().LoadFromStringAsync(content, cancellationToken)
                : await new YamlAgentManifestLoader().LoadFromStringAsync(content, cancellationToken);
        }
        catch (Vais.Agents.Control.AgentManifestValidationException ex)
        {
            AnsiConsole.MarkupLine($"[red]error[/] manifest validation failed:");
            foreach (var err in ex.Errors)
            {
                AnsiConsole.MarkupLine($"  - {err}");
            }
            return ProblemDetailsParser.ExitUsageError;
        }

        if (manifests.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]warning[/] no manifests parsed from input");
            return ProblemDetailsParser.ExitUsageError;
        }

        var config = VaisConfigFile.LoadOrDefault();
        var client = ClientFactory.Create(config, settings.Context, settings.Token);

        var anyError = false;
        foreach (var manifest in manifests)
        {
            var idempotencyKey = settings.IdempotencyKey ?? Guid.NewGuid().ToString("N");
            try
            {
                var handle = await client.CreateAsync(manifest, idempotencyKey, cancellationToken);
                AnsiConsole.MarkupLine($"{handle.AgentId} [green]created[/] (version {handle.Version})");
            }
            catch (AgentControlPlaneException ex) when (ProblemDetailsParser.IsConflict(ex))
            {
                // Conflict on create → fall back to update.
                try
                {
                    var updated = await client.UpdateAsync(manifest.Id, manifest, manifest.Version, idempotencyKey, cancellationToken);
                    AnsiConsole.MarkupLine($"{updated.AgentId} [blue]updated[/] (version {updated.Version})");
                }
                catch (AgentControlPlaneException updateEx)
                {
                    anyError = true;
                    var code = ProblemDetailsParser.HandleAndExitCode(updateEx, AnsiConsole.Console);
                    if (code != ProblemDetailsParser.ExitApiError)
                    {
                        return code;
                    }
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

    private static bool IsJson(string pathOrHint)
        => pathOrHint.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
}
