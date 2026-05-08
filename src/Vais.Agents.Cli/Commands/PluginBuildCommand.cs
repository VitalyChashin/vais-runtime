// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Vais.Agents.Cli.Commands;

/// <summary>
/// Builds a container plugin image via <c>docker build</c> and optionally
/// pushes it to the registry. Reads <c>plugin.yaml</c> in the build context
/// directory when <c>--image</c> is not supplied.
/// </summary>
internal sealed class PluginBuildCommand : AsyncCommand<PluginBuildCommand.Settings>
{
    internal static Func<string, CancellationToken, Task<int>> DockerRun = DockerRunner.RunAsync;

    public sealed class Settings : CommandSettings
    {
        [Description("Container image tag. When omitted, read from plugin.yaml spec.image.")]
        [CommandOption("--image")]
        public string? Image { get; init; }

        [Description("Build context directory. Defaults to current directory.")]
        [CommandOption("--context")]
        public string? BuildContext { get; init; }

        [Description("Push the image to the registry after a successful build.")]
        [CommandOption("--push")]
        [DefaultValue(false)]
        public bool Push { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var contextDir = Path.GetFullPath(settings.BuildContext ?? Directory.GetCurrentDirectory());

        var image = settings.Image ?? ReadImageFromPluginYaml(contextDir);
        if (string.IsNullOrWhiteSpace(image))
        {
            AnsiConsole.MarkupLine("[red]error[/] specify --image or add spec.image to plugin.yaml");
            return ProblemDetailsParser.ExitUsageError;
        }

        AnsiConsole.MarkupLine($"Building [bold]{Markup.Escape(image)}[/] from [grey]{Markup.Escape(contextDir)}[/]");

        var buildExit = await DockerRun($"build -t {image} {contextDir}", cancellationToken);
        if (buildExit != 0)
        {
            AnsiConsole.MarkupLine($"[red]✗[/] docker build failed (exit {buildExit})");
            return ProblemDetailsParser.ExitApiError;
        }

        AnsiConsole.MarkupLine($"[green]✓[/] built {Markup.Escape(image)}");

        if (!settings.Push)
            return ProblemDetailsParser.ExitSuccess;

        AnsiConsole.MarkupLine($"Pushing [bold]{Markup.Escape(image)}[/]");

        var pushExit = await DockerRun($"push {image}", cancellationToken);
        if (pushExit != 0)
        {
            AnsiConsole.MarkupLine($"[red]✗[/] docker push failed (exit {pushExit})");
            return ProblemDetailsParser.ExitApiError;
        }

        AnsiConsole.MarkupLine($"[green]✓[/] pushed {Markup.Escape(image)}");
        return ProblemDetailsParser.ExitSuccess;
    }

    internal static string? ReadImageFromPluginYaml(string directory)
    {
        var yamlPath = Path.Combine(directory, "plugin.yaml");
        if (!File.Exists(yamlPath)) return null;

        foreach (var line in File.ReadLines(yamlPath))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("image:", StringComparison.OrdinalIgnoreCase))
                return trimmed["image:".Length..].Trim();
        }
        return null;
    }
}
