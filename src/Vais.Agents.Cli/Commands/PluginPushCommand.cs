// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Vais.Agents.Cli.Plugins;
using Vais.Agents.Control.Http;

namespace Vais.Agents.Cli.Commands;

/// <summary>
/// Three modes depending on arguments:
/// <list type="bullet">
/// <item>
///   <term>DLL push</term>
///   <description>When <c>--dll</c> is supplied, or the positional argument ends
///   with <c>.dll</c>, hot-reloads a C# plugin via
///   <c>POST /v1/plugins/{name}/dll</c>.</description>
/// </item>
/// <item>
///   <term>Source push (default)</term>
///   <description>Packs a Python plugin source directory and hot-reloads via
///   <c>POST /v1/plugins/{name}/source</c>.</description>
/// </item>
/// <item>
///   <term>Image push</term>
///   <description>When <c>--image</c> is supplied, or the positional argument
///   looks like an image reference (contains <c>/</c> or <c>:</c>), runs
///   <c>docker push</c> then notifies the runtime via
///   <c>POST /v1/plugins/{name}/image</c>.</description>
/// </item>
/// </list>
/// </summary>
internal sealed class PluginPushCommand : AsyncCommand<PluginPushCommand.Settings>
{
    internal static Func<string, CancellationToken, Task<int>> DockerRun = DockerRunner.RunAsync;

    public sealed class Settings : CommandSettings
    {
        [Description("Plugin name (source mode) or image reference (image mode). " +
                     "Image mode is inferred when this value contains '/' or ':'.")]
        [CommandArgument(0, "<plugin-or-image>")]
        public string PluginOrImage { get; init; } = "";

        [Description("Explicit image reference. Forces image push mode.")]
        [CommandOption("--image")]
        public string? Image { get; init; }

        [Description("Plugin name in the runtime (image mode). " +
                     "When omitted, inferred from the image name.")]
        [CommandOption("--plugin")]
        public string? Plugin { get; init; }

        [Description("Path to a compiled .NET assembly (.dll) for C# plugin push. Forces DLL push mode.")]
        [CommandOption("--dll")]
        public string? Dll { get; init; }

        [Description("Source directory to pack and push (source mode). Defaults to the current directory (plugin root).")]
        [CommandOption("--source")]
        public string? Source { get; init; }

        [Description("Override the active context from ~/.vais/config.yaml.")]
        [CommandOption("--context")]
        public string? Context { get; init; }

        [Description("Bearer token override (wins over VAIS_TOKEN + config).")]
        [CommandOption("--token")]
        public string? Token { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var isDllMode = settings.Dll is not null
            || settings.PluginOrImage.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        if (isDllMode)
            return await ExecuteDllPushAsync(settings, cancellationToken);

        var isImageMode = settings.Image is not null
            || settings.PluginOrImage.Contains('/', StringComparison.Ordinal)
            || settings.PluginOrImage.Contains(':', StringComparison.Ordinal);

        return isImageMode
            ? await ExecuteImagePushAsync(settings, cancellationToken)
            : await ExecuteSourcePushAsync(settings, cancellationToken);
    }

    private static async Task<int> ExecuteDllPushAsync(Settings settings, CancellationToken cancellationToken)
    {
        var dllPath = settings.Dll ?? settings.PluginOrImage;
        if (!File.Exists(dllPath))
        {
            AnsiConsole.MarkupLine($"[red]error[/] DLL not found: {Markup.Escape(dllPath)}");
            return ProblemDetailsParser.ExitUsageError;
        }

        var pluginName = settings.Plugin ?? Path.GetFileNameWithoutExtension(dllPath);

        // CLI-side ABI pre-validation — friendly error before any upload.
        var abi = DllAbiReader.TryReadAbi(dllPath);
        if (abi is null)
        {
            AnsiConsole.MarkupLine("[red]✗[/] DLL has no [VaisPlugin] attribute. Add it and rebuild.");
            return ProblemDetailsParser.ExitUsageError;
        }
        if (!DllAbiReader.AbiMatches(abi, DllAbiReader.RuntimeAbiVersion))
        {
            AnsiConsole.MarkupLine(
                $"[red]✗[/] ABI mismatch: DLL targets [bold]{Markup.Escape(abi)}[/], runtime expects [bold]{DllAbiReader.RuntimeAbiVersion}[/]");
            AnsiConsole.MarkupLine(
                $"  Update [[assembly: VaisPlugin(targetApiVersion: \"{DllAbiReader.RuntimeAbiVersion}\")]] and rebuild.");
            return ProblemDetailsParser.ExitUsageError;
        }

        var config = VaisConfigFile.LoadOrDefault();
        var client = ClientFactory.Create(config, settings.Context, settings.Token);

        try
        {
            PluginDllPushResponse? result = null;
            await AnsiConsole.Status()
                .StartAsync($"Pushing {Markup.Escape(pluginName)}...", async _ =>
                {
                    await using var stream = File.OpenRead(dllPath);
                    result = await client.PushPluginDllAsync(pluginName, stream, "application/octet-stream", cancellationToken);
                });

            if (result!.Status is PluginDllPushStatus.Success or PluginDllPushStatus.Bootstrapped)
            {
                var verb = result.Status == PluginDllPushStatus.Bootstrapped ? "bootstrapped" : "reloaded";
                var handlers = result.Handlers is { Count: > 0 }
                    ? string.Join(", ", result.Handlers)
                    : "—";
                AnsiConsole.MarkupLine(
                    $"[green]✓[/] {Markup.Escape(pluginName)} {verb} (handlers: {Markup.Escape(handlers)})");
                return ProblemDetailsParser.ExitSuccess;
            }

            var detail = result.ErrorMessage ?? result.Status.ToString();
            AnsiConsole.MarkupLine(
                $"[red]✗[/] reload failed: {Markup.Escape(result.Status.ToString())} — {Markup.Escape(detail)}");
            return ProblemDetailsParser.ExitApiError;
        }
        catch (AgentControlPlaneException ex)
        {
            return ProblemDetailsParser.HandleAndExitCode(ex, AnsiConsole.Console);
        }
    }

    private static async Task<int> ExecuteImagePushAsync(Settings settings, CancellationToken cancellationToken)
    {
        var image = settings.Image ?? settings.PluginOrImage;
        var pluginName = settings.Plugin ?? InferPluginName(image);

        AnsiConsole.MarkupLine($"Pushing image [bold]{Markup.Escape(image)}[/]");
        var dockerExit = await DockerRun($"push {image}", cancellationToken);
        if (dockerExit != 0)
        {
            AnsiConsole.MarkupLine($"[red]✗[/] docker push failed (exit {dockerExit})");
            return ProblemDetailsParser.ExitApiError;
        }

        AnsiConsole.MarkupLine($"[green]✓[/] pushed {Markup.Escape(image)}");

        var config = VaisConfigFile.LoadOrDefault();
        var client = ClientFactory.Create(config, settings.Context, settings.Token);

        try
        {
            PluginImageUpdateResponse? result = null;
            await AnsiConsole.Status()
                .StartAsync($"Notifying runtime for {pluginName}...", async _ =>
                {
                    result = await client.PushPluginImageAsync(pluginName, image, cancellationToken);
                });

            if (result!.Status == PluginImageUpdateStatus.Success)
            {
                AnsiConsole.MarkupLine($"[green]✓[/] {Markup.Escape(pluginName)} container replaced");
                return ProblemDetailsParser.ExitSuccess;
            }

            if (result.Status == PluginImageUpdateStatus.RolloutStarted)
            {
                AnsiConsole.MarkupLine(
                    $"[green]✓[/] {Markup.Escape(pluginName)} Kubernetes rollout started — " +
                    "use [bold]kubectl rollout status[/] to track progress");
                return ProblemDetailsParser.ExitSuccess;
            }

            var urn = result.FailureUrn ?? result.Status.ToString();
            AnsiConsole.MarkupLine($"[red]✗[/] runtime update failed: {Markup.Escape(result.Status.ToString())} — {Markup.Escape(urn)}");
            return ProblemDetailsParser.ExitApiError;
        }
        catch (AgentControlPlaneException ex)
        {
            return ProblemDetailsParser.HandleAndExitCode(ex, AnsiConsole.Console);
        }
    }

    private static async Task<int> ExecuteSourcePushAsync(Settings settings, CancellationToken cancellationToken)
    {
        var pluginName = settings.PluginOrImage;
        var sourceDir = Path.GetFullPath(settings.Source ?? Directory.GetCurrentDirectory());
        if (!Directory.Exists(sourceDir))
        {
            AnsiConsole.MarkupLine($"[red]error[/] source directory not found: {Markup.Escape(sourceDir)}");
            return ProblemDetailsParser.ExitUsageError;
        }

        var config = VaisConfigFile.LoadOrDefault();
        var client = ClientFactory.Create(config, settings.Context, settings.Token);

        try
        {
            PluginSourcePushResponse? result = null;
            await AnsiConsole.Status()
                .StartAsync($"Pushing {pluginName}...", async _ =>
                {
                    using var archive = PluginSourcePacker.Pack(sourceDir);
                    result = await client.PushPluginSourceAsync(pluginName, archive, cancellationToken);
                });

            if (result!.Status is PluginSourcePushStatus.Success or PluginSourcePushStatus.Bootstrapped)
            {
                var pid = result.ProcessId?.ToString() ?? "?";
                var verb = result.Status == PluginSourcePushStatus.Bootstrapped ? "bootstrapped" : "reloaded";
                AnsiConsole.MarkupLine($"[green]✓[/] {Markup.Escape(pluginName)} {verb} (PID {pid})");
                return ProblemDetailsParser.ExitSuccess;
            }

            var detail = result.ErrorMessage ?? result.Status.ToString();
            AnsiConsole.MarkupLine($"[red]✗[/] reload failed: {Markup.Escape(result.Status.ToString())} — {Markup.Escape(detail)}");
            return ProblemDetailsParser.ExitApiError;
        }
        catch (AgentControlPlaneException ex)
        {
            return ProblemDetailsParser.HandleAndExitCode(ex, AnsiConsole.Console);
        }
    }

    /// <summary>
    /// Infers a plugin name from an image reference by stripping the registry
    /// prefix and tag. For example <c>registry.io/my-plugin:1.0</c> → <c>my-plugin</c>.
    /// </summary>
    internal static string InferPluginName(string image)
    {
        var name = image;
        var slash = name.LastIndexOf('/');
        if (slash >= 0) name = name[(slash + 1)..];
        var colon = name.IndexOf(':');
        if (colon >= 0) name = name[..colon];
        return name;
    }
}
