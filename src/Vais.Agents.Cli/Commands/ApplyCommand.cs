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
/// <para>
/// For <c>ContainerPlugin</c> manifests with a <c>spec.build</c> block, the CLI
/// runs <c>docker build</c> (and optionally <c>docker push</c>) before posting the
/// manifest to the control plane. Pass <c>--no-build</c> to skip this step.
/// </para>
/// </summary>
internal sealed class ApplyCommand : AsyncCommand<ApplyCommand.Settings>
{
    internal static Func<string, CancellationToken, Task<int>> DockerRun = DockerRunner.RunAsync;
    internal static Func<string, CancellationToken, Task<bool>> DockerImageExists = DockerRunner.ImageExistsAsync;

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

        [Description("Skip the local docker build step even when spec.build is present. The manifest is applied as-is.")]
        [CommandOption("--no-build")]
        public bool NoBuild { get; init; }
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
                    case ManifestResource.LlmGatewayConfigCase llmCase:
                        await ApplyLlmGatewayConfigAsync(client, llmCase.Config, idempotencyKey, cancellationToken);
                        break;
                    case ManifestResource.McpGatewayConfigCase mcpGwCase:
                        await ApplyMcpGatewayConfigAsync(client, mcpGwCase.Config, idempotencyKey, cancellationToken);
                        break;
                    case ManifestResource.McpServerCase mcpServerCase:
                        await ApplyMcpServerAsync(client, mcpServerCase.Server, idempotencyKey, cancellationToken);
                        break;
                    case ManifestResource.ContainerPluginCase containerPluginCase:
                        if (!await ApplyContainerPluginAsync(client, containerPluginCase.Manifest, idempotencyKey,
                                settings.File, settings.NoBuild, cancellationToken))
                            anyError = true;
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

    private static async Task ApplyLlmGatewayConfigAsync(IAgentControlPlaneClient client, LlmGatewayConfigManifest manifest, string idempotencyKey, CancellationToken ct)
    {
        try
        {
            var handle = await client.CreateLlmGatewayConfigAsync(manifest, idempotencyKey, ct);
            AnsiConsole.MarkupLine($"{handle.Id} [green]created[/] (llm-gateway, version {handle.Version})");
        }
        catch (AgentControlPlaneException ex) when (ProblemDetailsParser.IsConflict(ex))
        {
            var updated = await client.UpdateLlmGatewayConfigAsync(manifest.Id, manifest, manifest.Version, idempotencyKey, ct);
            AnsiConsole.MarkupLine($"{updated.Id} [blue]updated[/] (llm-gateway, version {updated.Version})");
        }
    }

    private static async Task ApplyMcpGatewayConfigAsync(IAgentControlPlaneClient client, McpGatewayConfigManifest manifest, string idempotencyKey, CancellationToken ct)
    {
        try
        {
            var handle = await client.CreateMcpGatewayConfigAsync(manifest, idempotencyKey, ct);
            AnsiConsole.MarkupLine($"{handle.Id} [green]created[/] (mcp-gateway, version {handle.Version})");
        }
        catch (AgentControlPlaneException ex) when (ProblemDetailsParser.IsConflict(ex))
        {
            var updated = await client.UpdateMcpGatewayConfigAsync(manifest.Id, manifest, manifest.Version, idempotencyKey, ct);
            AnsiConsole.MarkupLine($"{updated.Id} [blue]updated[/] (mcp-gateway, version {updated.Version})");
        }
    }

    private static async Task ApplyMcpServerAsync(IAgentControlPlaneClient client, McpServerManifest manifest, string idempotencyKey, CancellationToken ct)
    {
        try
        {
            var handle = await client.CreateMcpServerAsync(manifest, idempotencyKey, ct);
            AnsiConsole.MarkupLine($"{handle.Id} [green]created[/] (mcp-server, version {handle.Version})");
        }
        catch (AgentControlPlaneException ex) when (ProblemDetailsParser.IsConflict(ex))
        {
            var updated = await client.UpdateMcpServerAsync(manifest.Id, manifest, manifest.Version, idempotencyKey, ct);
            AnsiConsole.MarkupLine($"{updated.Id} [blue]updated[/] (mcp-server, version {updated.Version})");
        }
    }

    internal static async Task<bool> ApplyContainerPluginAsync(
        IAgentControlPlaneClient client,
        ContainerPluginManifest manifest,
        string idempotencyKey,
        string manifestFilePath,
        bool noBuild,
        CancellationToken ct)
    {
        if (manifest.Spec?.Build is { } build && !noBuild)
        {
            var manifestDir = manifestFilePath == "-"
                ? Directory.GetCurrentDirectory()
                : Path.GetDirectoryName(Path.GetFullPath(manifestFilePath)) ?? Directory.GetCurrentDirectory();

            var contextPath = Path.GetFullPath(Path.Combine(manifestDir, build.Context));
            var dockerfilePath = Path.GetFullPath(Path.Combine(contextPath, build.Dockerfile));

            if (!await DockerImageExists(manifest.Spec.Image, ct))
            {
                AnsiConsole.MarkupLine($"Building [bold]{Markup.Escape(manifest.Spec.Image)}[/] from [grey]{Markup.Escape(contextPath)}[/]");

                var buildArgsStr = build.Args is { Count: > 0 }
                    ? string.Join(" ", build.Args.Select(kv => $"--build-arg {kv.Key}={kv.Value}"))
                    : "";
                var buildDockerArgs = string.IsNullOrEmpty(buildArgsStr)
                    ? $"build -t {manifest.Spec.Image} -f {dockerfilePath} {contextPath}"
                    : $"build -t {manifest.Spec.Image} -f {dockerfilePath} {buildArgsStr} {contextPath}";

                var buildExit = await DockerRun(buildDockerArgs, ct);
                if (buildExit != 0)
                {
                    AnsiConsole.MarkupLine($"[red]✗[/] docker build failed (exit {buildExit})");
                    return false;
                }
                AnsiConsole.MarkupLine($"[green]✓[/] built {Markup.Escape(manifest.Spec.Image)}");

                if (build.Push)
                {
                    AnsiConsole.MarkupLine($"Pushing [bold]{Markup.Escape(manifest.Spec.Image)}[/]");
                    var pushExit = await DockerRun($"push {manifest.Spec.Image}", ct);
                    if (pushExit != 0)
                    {
                        AnsiConsole.MarkupLine($"[red]✗[/] docker push failed (exit {pushExit})");
                        return false;
                    }
                    AnsiConsole.MarkupLine($"[green]✓[/] pushed {Markup.Escape(manifest.Spec.Image)}");
                }
            }
        }

        try
        {
            var handle = await client.CreateContainerPluginAsync(manifest, idempotencyKey, ct);
            AnsiConsole.MarkupLine($"{handle.Id} [green]created[/] (container-plugin, version {handle.Version})");
        }
        catch (AgentControlPlaneException ex) when (ProblemDetailsParser.IsConflict(ex))
        {
            var updated = await client.UpdateContainerPluginAsync(manifest.Id, manifest, manifest.Version, idempotencyKey, ct);
            AnsiConsole.MarkupLine($"{updated.Id} [blue]updated[/] (container-plugin, version {updated.Version})");
        }
        return true;
    }

    private static bool IsJson(string pathOrHint)
        => pathOrHint.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
}
