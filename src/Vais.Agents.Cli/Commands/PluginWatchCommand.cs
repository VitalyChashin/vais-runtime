// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Vais.Agents.Control.Http;

namespace Vais.Agents.Cli.Commands;

/// <summary>
/// Watches a Python plugin source directory and hot-reloads the plugin on every change.
/// Uses a debounce window to coalesce rapid edits before pushing.
/// </summary>
internal sealed class PluginWatchCommand : AsyncCommand<PluginWatchCommand.Settings>
{
    private static readonly HashSet<string> WatchedExtensions = [".py", ".yaml", ".yml", ".toml", ".json", ".txt"];

    public sealed class Settings : CommandSettings
    {
        [Description("Name of the Python plugin to reload.")]
        [CommandArgument(0, "<plugin-name>")]
        public string PluginName { get; init; } = "";

        [Description("Directory to watch. Defaults to the current directory (plugin root).")]
        [CommandOption("--source")]
        public string? Source { get; init; }

        [Description("Debounce interval in milliseconds. Default: 500")]
        [CommandOption("--debounce")]
        public int DebounceMs { get; init; } = 500;

        [Description("Override the active context from ~/.vais/config.yaml.")]
        [CommandOption("--context")]
        public string? Context { get; init; }

        [Description("Bearer token override (wins over VAIS_TOKEN + config).")]
        [CommandOption("--token")]
        public string? Token { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var sourceDir = Path.GetFullPath(settings.Source ?? Directory.GetCurrentDirectory());
        if (!Directory.Exists(sourceDir))
        {
            AnsiConsole.MarkupLine($"[red]error[/] source directory not found: {Markup.Escape(sourceDir)}");
            return ProblemDetailsParser.ExitUsageError;
        }

        var config = VaisConfigFile.LoadOrDefault();
        var client = ClientFactory.Create(config, settings.Context, settings.Token);

        AnsiConsole.MarkupLine($"Watching [grey]{Markup.Escape(sourceDir)}[/] — press Ctrl+C to stop.");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        Console.CancelKeyPress += onCancel;

        CancellationTokenSource? debounceCts = null;
        var lockObj = new object();

        async Task FireAsync(CancellationToken ct)
        {
            var ts = DateTime.Now.ToString("HH:mm:ss");
            try
            {
                using var archive = PluginSourcePacker.Pack(sourceDir);
                var result = await client.PushPluginSourceAsync(settings.PluginName, archive, ct);
                if (result.Status == PluginSourcePushStatus.Success)
                {
                    var pid = result.ProcessId?.ToString() ?? "?";
                    AnsiConsole.MarkupLine($"[grey]{ts}[/] [green]✓[/] {Markup.Escape(settings.PluginName)} reloaded (PID {pid})");
                }
                else
                {
                    var detail = result.ErrorMessage ?? result.Status.ToString();
                    AnsiConsole.MarkupLine($"[grey]{ts}[/] [red]✗[/] reload failed: {Markup.Escape(result.Status.ToString())} — {Markup.Escape(detail)}");
                }
            }
            catch (OperationCanceledException) { }
            catch (AgentControlPlaneException ex)
            {
                AnsiConsole.MarkupLine($"[grey]{ts}[/] [red]error[/] {Markup.Escape(ex.Title ?? ex.Message)}");
            }
        }

        void OnChanged(object sender, FileSystemEventArgs e)
        {
            if (!WatchedExtensions.Contains(Path.GetExtension(e.Name ?? ""))) return;
            if (cts.IsCancellationRequested) return;

            CancellationTokenSource newCts;
            lock (lockObj)
            {
                debounceCts?.Cancel();
                debounceCts?.Dispose();
                newCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                debounceCts = newCts;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(settings.DebounceMs, newCts.Token);
                    await FireAsync(newCts.Token);
                }
                catch (OperationCanceledException) { }
            }, CancellationToken.None);
        }

        using var watcher = new FileSystemWatcher(sourceDir)
        {
            IncludeSubdirectories = true,
            EnableRaisingEvents = true,
        };
        watcher.Changed += OnChanged;
        watcher.Created += OnChanged;
        watcher.Renamed += (s, e) => OnChanged(s, e);

        try
        {
            await Task.Delay(Timeout.Infinite, cts.Token);
            return ProblemDetailsParser.ExitSuccess;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return ProblemDetailsParser.ExitSigInt;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
            lock (lockObj)
            {
                debounceCts?.Cancel();
                debounceCts?.Dispose();
            }
        }
    }
}
