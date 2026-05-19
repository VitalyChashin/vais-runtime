// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Vais.Agents.Cli.Plugins;
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

        [Description("Path to a compiled C# DLL to upload alongside a 'kind: Plugin' manifest (language: csharp).")]
        [CommandOption("--dll")]
        public string? Dll { get; init; }

        [Description("Acknowledge that a container extension targets a hot seam and may add per-call latency. Required when the server returns 412.")]
        [CommandOption("--accept-latency-cost")]
        public bool AcceptLatencyCost { get; init; }
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
                        if (!await ApplyMcpServerAsync(client, mcpServerCase.Server, idempotencyKey,
                                settings.File, settings.NoBuild, cancellationToken))
                            anyError = true;
                        break;
                    case ManifestResource.ContainerPluginCase containerPluginCase:
                        if (!await ApplyContainerPluginAsync(client, containerPluginCase.Manifest, idempotencyKey,
                                settings.File, settings.NoBuild, cancellationToken))
                            anyError = true;
                        break;
                    case ManifestResource.EvalSuiteCase evalSuiteCase:
                        await ApplyEvalSuiteAsync(client, evalSuiteCase.Suite, cancellationToken);
                        break;
                    case ManifestResource.PluginCase pluginCase:
                        if (!await ApplyPluginAsync(client, pluginCase.Plugin, settings.Dll, cancellationToken))
                            anyError = true;
                        break;
                    case ManifestResource.ExtensionCase extensionCase:
                        if (!await ApplyExtensionAsync(client, extensionCase.Extension, settings.File, settings.Dll, settings.AcceptLatencyCost, cancellationToken))
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

    internal static async Task<bool> ApplyMcpServerAsync(
        IAgentControlPlaneClient client,
        McpServerManifest manifest,
        string idempotencyKey,
        string manifestFilePath,
        bool noBuild,
        CancellationToken ct)
    {
        // CMS-5: build-on-apply for transport: containerStdio with spec.container.build.
        if (manifest.Container is { Build: { } build, Image: var imageOpt } && !noBuild)
        {
            var manifestDir = manifestFilePath == "-"
                ? Directory.GetCurrentDirectory()
                : Path.GetDirectoryName(Path.GetFullPath(manifestFilePath)) ?? Directory.GetCurrentDirectory();

            var contextPath = Path.GetFullPath(Path.Combine(manifestDir, build.Context));
            var dockerfilePath = Path.GetFullPath(Path.Combine(contextPath, build.Dockerfile));

            // Image tag derived from manifest id+version when spec.container.image is not set.
            var imageTag = imageOpt ?? $"vais-mcp-{manifest.Id}:{manifest.Version}";

            if (!await DockerImageExists(imageTag, ct))
            {
                AnsiConsole.MarkupLine($"Building [bold]{Markup.Escape(imageTag)}[/] from [grey]{Markup.Escape(contextPath)}[/]");

                var buildArgsStr = build.Args is { Count: > 0 }
                    ? string.Join(" ", build.Args.Select(kv => $"--build-arg {kv.Key}={kv.Value}"))
                    : "";
                var buildDockerArgs = string.IsNullOrEmpty(buildArgsStr)
                    ? $"build -t {imageTag} -f {dockerfilePath} {contextPath}"
                    : $"build -t {imageTag} -f {dockerfilePath} {buildArgsStr} {contextPath}";

                var buildExit = await DockerRun(buildDockerArgs, ct);
                if (buildExit != 0)
                {
                    AnsiConsole.MarkupLine($"[red]✗[/] docker build failed (exit {buildExit})");
                    return false;
                }
                AnsiConsole.MarkupLine($"[green]✓[/] built {Markup.Escape(imageTag)}");

                if (build.Push)
                {
                    AnsiConsole.MarkupLine($"Pushing [bold]{Markup.Escape(imageTag)}[/]");
                    var pushExit = await DockerRun($"push {imageTag}", ct);
                    if (pushExit != 0)
                    {
                        AnsiConsole.MarkupLine($"[red]✗[/] docker push failed (exit {pushExit})");
                        return false;
                    }
                    AnsiConsole.MarkupLine($"[green]✓[/] pushed {Markup.Escape(imageTag)}");
                }
            }

            // Patch the manifest in-flight so the server-side record carries the resolved image.
            // Clear the Build block too — once we have a tagged image the server has no use for the
            // local build context, and the loader rejects image+build set simultaneously.
            if (imageOpt is null)
                manifest = manifest with { Container = manifest.Container with { Image = imageTag, Build = null } };
            else
                manifest = manifest with { Container = manifest.Container with { Build = null } };
        }

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
        return true;
    }

    private static async Task ApplyEvalSuiteAsync(IAgentControlPlaneClient client, EvalSuiteManifest manifest, CancellationToken ct)
    {
        var result = await client.UpsertEvalSuiteAsync(manifest, ct);
        AnsiConsole.MarkupLine($"{result.Handle.Id} [green]applied[/] (eval-suite, version {result.Handle.Version})");
    }

    internal static async Task<bool> ApplyPluginAsync(
        IAgentControlPlaneClient client,
        PluginManifest manifest,
        string? dllPath,
        CancellationToken ct)
    {
        var language = manifest.Spec?.Language ?? string.Empty;

        if (!string.Equals(language, "csharp", StringComparison.OrdinalIgnoreCase))
        {
            AnsiConsole.MarkupLine($"{manifest.Id} [green]validated[/] (plugin, language: {Markup.Escape(language)})");
            return true;
        }

        if (string.IsNullOrWhiteSpace(dllPath))
        {
            AnsiConsole.MarkupLine(
                $"[yellow]hint[/] {manifest.Id}: pass [bold]--dll <path>[/] to upload the assembly with this manifest.");
            return true;
        }

        if (!File.Exists(dllPath))
        {
            AnsiConsole.MarkupLine($"[red]error[/] --dll file not found: {Markup.Escape(dllPath)}");
            return false;
        }

        var abi = DllAbiReader.TryReadAbi(dllPath);
        if (abi is null)
        {
            AnsiConsole.MarkupLine("[red]✗[/] DLL has no [VaisPlugin] attribute. Add it and rebuild.");
            return false;
        }
        if (!DllAbiReader.AbiMatches(abi, DllAbiReader.RuntimeAbiVersion))
        {
            AnsiConsole.MarkupLine(
                $"[red]✗[/] ABI mismatch: DLL targets [bold]{Markup.Escape(abi)}[/], runtime expects [bold]{DllAbiReader.RuntimeAbiVersion}[/]");
            return false;
        }

        try
        {
            PluginDllPushResponse? result = null;
            await AnsiConsole.Status().StartAsync($"Applying {Markup.Escape(manifest.Id)}…", async _ =>
            {
                await using var dllStream = File.OpenRead(dllPath);
                result = await client.ApplyPluginAsync(manifest, dllStream, ct);
            });

            if (result!.Status is PluginDllPushStatus.Success or PluginDllPushStatus.Bootstrapped)
            {
                var verb = result.Status == PluginDllPushStatus.Bootstrapped ? "created" : "updated";
                var handlers = result.Handlers is { Count: > 0 }
                    ? string.Join(", ", result.Handlers)
                    : "—";
                var color = result.Status == PluginDllPushStatus.Bootstrapped ? "green" : "blue";
                AnsiConsole.MarkupLine(
                    $"{manifest.Id} [{color}]{verb}[/] (plugin, handlers: {Markup.Escape(handlers)})");
                return true;
            }

            AnsiConsole.MarkupLine(
                $"[red]✗[/] {Markup.Escape(manifest.Id)}: {result.Status} — {Markup.Escape(result.ErrorMessage ?? result.Status.ToString())}");
            return false;
        }
        catch (AgentControlPlaneException ex)
        {
            ProblemDetailsParser.HandleAndExitCode(ex, AnsiConsole.Console);
            return false;
        }
    }

    private static async Task<bool> ApplyExtensionAsync(
        IAgentControlPlaneClient client,
        ExtensionManifest manifest,
        string manifestFilePath,
        string? dllPath,
        bool acceptLatencyCost,
        CancellationToken ct)
    {
        var host = manifest.Spec?.Host ?? string.Empty;

        if (string.Equals(host, "csharp", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(dllPath))
        {
            AnsiConsole.MarkupLine(
                $"[yellow]hint[/] {manifest.Id}: pass [bold]--dll <path>[/] to upload the assembly with this manifest.");
            return true;
        }

        if (!string.IsNullOrWhiteSpace(dllPath) && !File.Exists(dllPath))
        {
            AnsiConsole.MarkupLine($"[red]error[/] --dll file not found: {Markup.Escape(dllPath)}");
            return false;
        }

        string rawYaml;
        try
        {
            rawYaml = await File.ReadAllTextAsync(manifestFilePath, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]error[/] could not read manifest file: {Markup.Escape(ex.Message)}");
            return false;
        }

        try
        {
            ExtensionApplyResponse? result = null;
            await AnsiConsole.Status().StartAsync($"Applying {Markup.Escape(manifest.Id)}…", async _ =>
            {
                Stream? dllStream = string.IsNullOrWhiteSpace(dllPath) ? null : File.OpenRead(dllPath);
                await using (dllStream)
                {
                    result = await client.ApplyExtensionAsync(rawYaml, dllStream, acceptLatencyCost, ct);
                }
            });

            if (result!.Status is ExtensionApplyStatus.Success or ExtensionApplyStatus.Created)
            {
                var verb = result.Status == ExtensionApplyStatus.Created ? "created" : "updated";
                var handlers = result.Handlers is { Count: > 0 }
                    ? string.Join(", ", result.Handlers)
                    : "—";
                var color = result.Status == ExtensionApplyStatus.Created ? "green" : "blue";
                AnsiConsole.MarkupLine(
                    $"{manifest.Id} [{color}]{verb}[/] (extension, host: {Markup.Escape(host)}, handlers: {Markup.Escape(handlers)})");
                return true;
            }

            if (result.Status == ExtensionApplyStatus.ValidationFailed &&
                result.ErrorMessage?.Contains("hot-seam guard", StringComparison.OrdinalIgnoreCase) is true)
            {
                AnsiConsole.MarkupLine($"[yellow]latency-warning[/] {Markup.Escape(result.ErrorMessage)}");
                AnsiConsole.MarkupLine("Re-run with [bold]--accept-latency-cost[/] to proceed.");
                return false;
            }

            AnsiConsole.MarkupLine(
                $"[red]✗[/] {Markup.Escape(manifest.Id)}: {result.Status} — {Markup.Escape(result.ErrorMessage ?? result.Status.ToString())}");
            return false;
        }
        catch (AgentControlPlaneException ex)
        {
            ProblemDetailsParser.HandleAndExitCode(ex, AnsiConsole.Console);
            return false;
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
