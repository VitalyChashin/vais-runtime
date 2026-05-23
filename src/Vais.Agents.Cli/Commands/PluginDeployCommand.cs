// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.ComponentModel;
using System.Text;
using Spectre.Console;
using Spectre.Console.Cli;
using Vais.Agents.Control.Http;

namespace Vais.Agents.Cli.Commands;

/// <summary>
/// Deploys a container plugin to Kubernetes using <c>helm upgrade --install</c>
/// with the built-in <c>vais-plugin</c> Helm chart, then registers the plugin
/// with the running runtime via the control plane (create-or-update).
/// Pass <c>--no-apply</c> to skip the control-plane step (e.g. when the runtime
/// is not yet up during initial cluster bootstrap).
/// </summary>
internal sealed class PluginDeployCommand : AsyncCommand<PluginDeployCommand.Settings>
{
    internal static Func<string, CancellationToken, Task<int>> HelmRun = HelmRunner.RunAsync;

    public sealed class Settings : CommandSettings
    {
        [Description("Helm release name (also used as the plugin name in the runtime).")]
        [CommandArgument(0, "<release-name>")]
        public string ReleaseName { get; init; } = "";

        [Description("Container image reference (e.g. registry.io/my-plugin:1.2.3).")]
        [CommandOption("--image")]
        public required string Image { get; init; }

        [Description("Kubernetes namespace to deploy into. Default: default.")]
        [CommandOption("-n|--namespace")]
        public string Namespace { get; init; } = "default";

        [Description("Number of replicas. Default: 1.")]
        [CommandOption("--replicas")]
        public int Replicas { get; init; } = 1;

        [Description("Container port the plugin listens on. Default: 8080.")]
        [CommandOption("--port")]
        public int Port { get; init; } = 8080;

        [Description("Image pull policy passed to the Helm chart (e.g. Always, IfNotPresent, Never). Default: IfNotPresent.")]
        [CommandOption("--image-pull-policy")]
        public string ImagePullPolicy { get; init; } = "IfNotPresent";

        [Description("Enable an opt-in writable workspace of this size (MiB) mounted at --workspace-path. Omit to deploy with no workspace (default).")]
        [CommandOption("--workspace-size-mb")]
        public int? WorkspaceSizeMb { get; init; }

        [Description("Workspace mount path inside the container. Default: /workspace.")]
        [CommandOption("--workspace-path")]
        public string WorkspacePath { get; init; } = "/workspace";

        [Description("Workspace storage backend: disk | memory. Default: disk.")]
        [CommandOption("--workspace-medium")]
        public string WorkspaceMedium { get; init; } = "disk";

        [Description("Back the workspace with a PersistentVolumeClaim that survives pod restarts (otherwise an emptyDir).")]
        [CommandOption("--workspace-persist")]
        public bool WorkspacePersist { get; init; }

        [Description("StorageClass for the persistent workspace PVC. Empty = cluster default.")]
        [CommandOption("--workspace-storage-class")]
        public string? WorkspaceStorageClass { get; init; }

        [Description("Path to a custom Helm values file. When omitted, uses the built-in chart defaults.")]
        [CommandOption("-f|--values")]
        public string? ValuesFile { get; init; }

        [Description("Additional --set overrides passed verbatim to helm (e.g. 'resources.limits.cpu=1').")]
        [CommandOption("--set")]
        public string[]? SetValues { get; init; }

        [Description("Dry-run mode: pass --dry-run to helm without installing.")]
        [CommandOption("--dry-run")]
        public bool DryRun { get; init; }

        [Description("Skip registering the plugin with the runtime control plane after the Helm install.")]
        [CommandOption("--no-apply")]
        public bool NoApply { get; init; }

        [Description("Override the active kubeconfig context for the vais runtime connection.")]
        [CommandOption("--context")]
        public string? Context { get; init; }

        [Description("Bearer token for the vais runtime control plane. Overrides the token in the active context.")]
        [CommandOption("--token")]
        public string? Token { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        string chartDir;
        bool cleanupChart;
        try
        {
            chartDir = EmbeddedChartExtractor.ExtractToTemp();
            cleanupChart = true;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]error[/] Failed to extract built-in chart: {Markup.Escape(ex.Message)}");
            return ProblemDetailsParser.ExitApiError;
        }

        try
        {
            var args = BuildHelmArgs(settings, chartDir);
            AnsiConsole.MarkupLine($"Running: [dim]helm {Markup.Escape(args)}[/]");
            var exit = await HelmRun(args, cancellationToken);

            if (exit != 0)
            {
                AnsiConsole.MarkupLine($"[red]✗[/] helm exited with code {exit}");
                return ProblemDetailsParser.ExitApiError;
            }

            if (settings.DryRun)
                return ProblemDetailsParser.ExitSuccess;

            AnsiConsole.MarkupLine(
                $"[green]✓[/] {Markup.Escape(settings.ReleaseName)} deployed to namespace {Markup.Escape(settings.Namespace)}");

            if (settings.NoApply)
                return ProblemDetailsParser.ExitSuccess;

            var cfg = VaisConfigFile.LoadOrDefault();
            var client = ClientFactory.Create(cfg, settings.Context, settings.Token);
            var manifest = BuildManifest(settings);
            var idempotencyKey = Guid.NewGuid().ToString("N");

            AnsiConsole.MarkupLine("Registering plugin with runtime...");
            try
            {
                var handle = await client.CreateContainerPluginAsync(manifest, idempotencyKey, cancellationToken);
                AnsiConsole.MarkupLine($"{Markup.Escape(handle.Id)} [green]registered[/] (container-plugin, version {Markup.Escape(handle.Version)})");
            }
            catch (AgentControlPlaneException ex) when (ProblemDetailsParser.IsConflict(ex))
            {
                var updated = await client.UpdateContainerPluginAsync(manifest.Id, manifest, manifest.Version, idempotencyKey, cancellationToken);
                AnsiConsole.MarkupLine($"{Markup.Escape(updated.Id)} [blue]updated[/] (container-plugin, version {Markup.Escape(updated.Version)})");
            }

            return ProblemDetailsParser.ExitSuccess;
        }
        finally
        {
            if (cleanupChart)
            {
                try { Directory.Delete(chartDir, recursive: true); }
                catch { /* best-effort */ }
            }
        }
    }

    internal static string BuildHelmArgs(Settings settings, string chartDir)
    {
        var sb = new StringBuilder();
        sb.Append($"upgrade --install {settings.ReleaseName} {chartDir}");
        sb.Append($" --namespace {settings.Namespace}");
        sb.Append($" --create-namespace");
        sb.Append($" --set pluginName={settings.ReleaseName}");
        sb.Append($" --set image.repository={ImageRepository(settings.Image)}");
        sb.Append($" --set image.tag={ImageTag(settings.Image)}");
        sb.Append($" --set image.pullPolicy={settings.ImagePullPolicy}");
        sb.Append($" --set replicaCount={settings.Replicas}");
        sb.Append($" --set pluginPort={settings.Port}");

        if (settings.WorkspaceSizeMb is { } workspaceSizeMb)
        {
            sb.Append($" --set workspace.enabled=true");
            sb.Append($" --set workspace.path={settings.WorkspacePath}");
            sb.Append($" --set workspace.sizeMb={workspaceSizeMb}");
            sb.Append($" --set workspace.medium={settings.WorkspaceMedium}");
            sb.Append($" --set workspace.persist={(settings.WorkspacePersist ? "true" : "false")}");
            if (!string.IsNullOrEmpty(settings.WorkspaceStorageClass))
                sb.Append($" --set workspace.storageClassName={settings.WorkspaceStorageClass}");
        }

        if (settings.ValuesFile is not null)
            sb.Append($" -f {settings.ValuesFile}");

        if (settings.SetValues is { Length: > 0 })
            foreach (var kv in settings.SetValues)
                sb.Append($" --set {kv}");

        if (settings.DryRun)
            sb.Append(" --dry-run");

        return sb.ToString();
    }

    internal static ContainerPluginManifest BuildManifest(Settings settings)
    {
        var serviceUrl = $"http://{settings.ReleaseName}.{settings.Namespace}.svc.cluster.local:{settings.Port}";
        return new ContainerPluginManifest(settings.ReleaseName, Version: "1.0")
        {
            Spec = new ContainerPluginSpec
            {
                Image = settings.Image,
                Port = settings.Port,
                Topology = "kubernetes",
                Kubernetes = new ContainerPluginKubernetesConfig
                {
                    ServiceUrl = serviceUrl,
                    DeploymentName = settings.ReleaseName,
                    Namespace = settings.Namespace,
                },
                Workspace = settings.WorkspaceSizeMb is { } sizeMb
                    ? new ContainerPluginWorkspaceSpec
                    {
                        Path = settings.WorkspacePath,
                        SizeMb = sizeMb,
                        Medium = settings.WorkspaceMedium,
                        Persist = settings.WorkspacePersist,
                    }
                    : null,
            },
        };
    }

    private static string ImageRepository(string image)
    {
        var colon = image.LastIndexOf(':');
        return colon >= 0 ? image[..colon] : image;
    }

    private static string ImageTag(string image)
    {
        var colon = image.LastIndexOf(':');
        return colon >= 0 ? image[(colon + 1)..] : "latest";
    }
}
