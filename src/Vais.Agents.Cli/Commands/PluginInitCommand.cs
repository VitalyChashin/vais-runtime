// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Vais.Agents.Cli.Commands;

/// <summary>
/// Scaffolds a <c>plugin.yaml</c> (and optionally a <c>Dockerfile</c>)
/// in the current (or specified) output directory.
/// </summary>
internal sealed class PluginInitCommand : Command<PluginInitCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Plugin runtime: python or dotnet.")]
        [CommandOption("--runtime")]
        public string Runtime { get; init; } = "python";

        [Description("Plugin name. Defaults to the current directory name.")]
        [CommandOption("--name")]
        public string? Name { get; init; }

        [Description("Output directory. Defaults to the current directory.")]
        [CommandOption("-o|--output")]
        public string? Output { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var runtime = settings.Runtime.Trim().ToLowerInvariant();
        if (runtime is not ("python" or "dotnet"))
        {
            AnsiConsole.MarkupLine("[red]error[/] --runtime must be 'python' or 'dotnet'");
            return ProblemDetailsParser.ExitUsageError;
        }

        var outDir = Path.GetFullPath(settings.Output ?? Directory.GetCurrentDirectory());
        Directory.CreateDirectory(outDir);

        var name = string.IsNullOrWhiteSpace(settings.Name)
            ? new DirectoryInfo(outDir).Name
            : settings.Name.Trim();

        var yamlPath = Path.Combine(outDir, "plugin.yaml");
        if (File.Exists(yamlPath))
        {
            AnsiConsole.MarkupLine($"[red]error[/] {Markup.Escape(yamlPath)} already exists");
            return ProblemDetailsParser.ExitUsageError;
        }

        var (yaml, dockerfile) = runtime == "python"
            ? BuildPythonScaffold(name)
            : BuildDotnetScaffold(name);

        File.WriteAllText(yamlPath, yaml);
        AnsiConsole.MarkupLine($"[green]wrote[/] {Markup.Escape(yamlPath)}");

        if (dockerfile is not null)
        {
            var dockerfilePath = Path.Combine(outDir, "Dockerfile");
            if (!File.Exists(dockerfilePath))
            {
                File.WriteAllText(dockerfilePath, dockerfile);
                AnsiConsole.MarkupLine($"[green]wrote[/] {Markup.Escape(dockerfilePath)}");
            }
        }

        return ProblemDetailsParser.ExitSuccess;
    }

    internal static (string Yaml, string? Dockerfile) BuildPythonScaffold(string name) =>
    (
$@"apiVersion: vais.agents/v1
kind: Plugin
metadata:
  name: {name}
spec:
  runtime: python
  entrypoint: src/server.py
  python:
    version: ""3.11""
    interpreter: .venv/bin/python
  health:
    handshakeTimeoutSeconds: 10
    restartPolicy: exponentialBackoff
    invokeTimeoutSeconds: 60
", null);

    internal static (string Yaml, string? Dockerfile) BuildDotnetScaffold(string name) =>
    (
$@"apiVersion: vais.agents/v1
kind: Plugin
metadata:
  name: {name}
spec:
  runtime: container
  image: my-registry/{name}:latest
  port: 8080
",
$@"FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app .
USER 65532:65532
EXPOSE 8080
ENTRYPOINT [""dotnet"", ""{name}.dll""]
");
}
