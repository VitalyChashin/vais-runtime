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
/// Validates an MCP gateway config manifest file without registering it.
/// Performs structural checks locally then sends the parsed manifest to
/// <c>POST /v1/mcp-gateways/validate</c>. Exits 0 on pass, 1 on any error.
/// </summary>
internal sealed class McpGatewayValidateCommand : AsyncCommand<McpGatewayValidateCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Path to the MCP gateway manifest file (.yaml / .yml / .json). Use '-' to read from stdin as YAML.")]
        [CommandOption("-f|--file")]
        public required string File { get; init; }

        [Description("Output format: text | json | yaml. Default: text.")]
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

        IReadOnlyList<McpGatewayConfigManifest> manifests;
        try
        {
            var resources = IsJson(filenameHint)
                ? await new JsonAgentGraphManifestLoader().LoadAllResourcesFromStringAsync(content, cancellationToken)
                : await new YamlAgentGraphManifestLoader().LoadAllResourcesFromStringAsync(content, cancellationToken);
            manifests = resources.OfType<ManifestResource.McpGatewayConfigCase>().Select(r => r.Config).ToList();
        }
        catch (AgentManifestValidationException ex)
        {
            AnsiConsole.MarkupLine("[red]error[/] manifest failed local structural validation:");
            foreach (var err in ex.Errors)
                AnsiConsole.MarkupLine($"  - {Markup.Escape(err)}");
            return ProblemDetailsParser.ExitUsageError;
        }

        if (manifests.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]error[/] no McpGatewayConfig manifest found in file");
            return ProblemDetailsParser.ExitUsageError;
        }

        if (manifests.Count > 1)
        {
            AnsiConsole.MarkupLine($"[red]error[/] file contains {manifests.Count} mcp-gateway manifests; mcp-gateway-validate accepts exactly one");
            return ProblemDetailsParser.ExitUsageError;
        }

        var manifest = manifests[0];
        var config = VaisConfigFile.LoadOrDefault();
        var client = ClientFactory.Create(config, settings.Context, settings.Token);

        McpGatewayConfigValidationResult result;
        try
        {
            result = await client.ValidateMcpGatewayConfigAsync(manifest, cancellationToken);
        }
        catch (AgentControlPlaneException ex)
        {
            return ProblemDetailsParser.HandleAndExitCode(ex, AnsiConsole.Console);
        }

        var format = OutputFormatter.Parse(settings.Output, OutputFormat.Table);
        if (format == OutputFormat.Json)
        {
            OutputFormatter.WriteJson(result, AnsiConsole.Console);
        }
        else if (format == OutputFormat.Yaml)
        {
            OutputFormatter.WriteYaml(result, AnsiConsole.Console);
        }
        else
        {
            if (result.Valid)
            {
                AnsiConsole.MarkupLine($"[green]valid[/] {Markup.Escape(manifest.Id)}");
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]invalid[/] {Markup.Escape(manifest.Id)}:");
                foreach (var err in result.Errors)
                    AnsiConsole.MarkupLine($"  - {Markup.Escape(err)}");
            }
        }

        return result.Valid ? ProblemDetailsParser.ExitSuccess : ProblemDetailsParser.ExitApiError;
    }

    private static bool IsJson(string pathOrHint)
        => pathOrHint.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
}
