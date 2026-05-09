// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.ComponentModel;
using System.Text;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Vais.Agents.Cli.Commands;

/// <summary>
/// Deploys a container plugin to Kubernetes using <c>helm upgrade --install</c>
/// with the built-in <c>vais-plugin</c> Helm chart.
/// Wraps <c>helm upgrade --install &lt;release&gt; &lt;chart&gt; [overrides]</c>.
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

        [Description("Path to a custom Helm values file. When omitted, uses the built-in chart defaults.")]
        [CommandOption("-f|--values")]
        public string? ValuesFile { get; init; }

        [Description("Additional --set overrides passed verbatim to helm (e.g. 'resources.limits.cpu=1').")]
        [CommandOption("--set")]
        public string[]? SetValues { get; init; }

        [Description("Dry-run mode: pass --dry-run to helm without installing.")]
        [CommandOption("--dry-run")]
        public bool DryRun { get; init; }
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

            if (exit == 0)
            {
                if (!settings.DryRun)
                    AnsiConsole.MarkupLine(
                        $"[green]✓[/] {Markup.Escape(settings.ReleaseName)} deployed to namespace {Markup.Escape(settings.Namespace)}");
                return ProblemDetailsParser.ExitSuccess;
            }

            AnsiConsole.MarkupLine($"[red]✗[/] helm exited with code {exit}");
            return ProblemDetailsParser.ExitApiError;
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
        sb.Append($" --set replicaCount={settings.Replicas}");
        sb.Append($" --set pluginPort={settings.Port}");

        if (settings.ValuesFile is not null)
            sb.Append($" -f {settings.ValuesFile}");

        if (settings.SetValues is { Length: > 0 })
            foreach (var kv in settings.SetValues)
                sb.Append($" --set {kv}");

        if (settings.DryRun)
            sb.Append(" --dry-run");

        return sb.ToString();
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
