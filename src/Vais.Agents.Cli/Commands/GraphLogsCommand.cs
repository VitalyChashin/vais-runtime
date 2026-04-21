// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.ComponentModel;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Cli;
using Vais.Agents.Control.Http;

namespace Vais.Agents.Cli.Commands;

/// <summary>
/// Streams events from a graph run via SSE. Opens an
/// <c>InvokeGraphStreamAsync</c> (or <c>ResumeGraphStreamAsync</c> when
/// <c>--run-id</c> + <c>--interrupt-id</c> are supplied) stream and prints
/// the full <see cref="AgentGraphEvent"/> taxonomy as events arrive.
/// Ctrl-C cancels and exits with POSIX <c>130</c>.
/// </summary>
internal sealed class GraphLogsCommand : AsyncCommand<GraphLogsCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Graph id whose run events to stream.")]
        [CommandArgument(0, "<id>")]
        public required string GraphId { get; init; }

        [Description("Initial state as JSON (inline or @file). Defaults to '{}'. Ignored when --interrupt-id is supplied.")]
        [CommandOption("--initial-state")]
        public string? InitialState { get; init; }

        [Description("Version to target.")]
        [CommandOption("--version")]
        public string? Version { get; init; }

        [Description("Caller-supplied run identifier (optional).")]
        [CommandOption("--run-id")]
        public string? RunId { get; init; }

        [Description("Resume from this interrupt id instead of starting a new run. Requires --run-id.")]
        [CommandOption("--interrupt-id")]
        public string? InterruptId { get; init; }

        [Description("Comma-separated event-kind filter (e.g. --only graph.started,node.completed). Unknown kinds ignored.")]
        [CommandOption("--only")]
        public string? Only { get; init; }

        [Description("Override the active context.")]
        [CommandOption("--context")]
        public string? Context { get; init; }

        [Description("Bearer token override (wins over VAIS_TOKEN + config).")]
        [CommandOption("--token")]
        public string? Token { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var kindFilter = ParseKindFilter(settings.Only);

        var config = VaisConfigFile.LoadOrDefault();
        var client = ClientFactory.Create(config, settings.Context, settings.Token);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cts.Cancel(); };
        Console.CancelKeyPress += onCancel;

        try
        {
            IAsyncEnumerable<AgentGraphEvent> stream;

            if (!string.IsNullOrWhiteSpace(settings.InterruptId))
            {
                if (string.IsNullOrWhiteSpace(settings.RunId))
                {
                    AnsiConsole.MarkupLine("[red]error[/] --run-id is required with --interrupt-id");
                    return ProblemDetailsParser.ExitUsageError;
                }
                var resumeRequest = new GraphResumeRequest(
                    RunId: settings.RunId,
                    InterruptId: settings.InterruptId);
                stream = client.ResumeGraphStreamAsync(settings.GraphId, settings.RunId, resumeRequest, settings.Version, idempotencyKey: null, cts.Token);
            }
            else
            {
                IDictionary<string, JsonElement> initialState;
                try
                {
                    var raw = ArgumentFileReader.Resolve(settings.InitialState);
                    initialState = InvokeGraphCommand.ParseStateBag(raw);
                }
                catch (JsonException ex)
                {
                    AnsiConsole.MarkupLine($"[red]error[/] --initial-state must be valid JSON: {ex.Message}");
                    return ProblemDetailsParser.ExitUsageError;
                }

                var request = new GraphInvocationRequest(
                    InitialState: initialState,
                    RunId: settings.RunId);
                stream = client.InvokeGraphStreamAsync(settings.GraphId, request, settings.Version, idempotencyKey: null, cts.Token);
            }

            var renderer = new GraphEventRenderer(AnsiConsole.Console);
            await foreach (var evt in stream.WithCancellation(cts.Token))
            {
                if (kindFilter is not null && !kindFilter.Contains(GraphEventRenderer.EventKindName(evt)))
                {
                    continue;
                }
                renderer.Render(evt);
            }
            return ProblemDetailsParser.ExitSuccess;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            AnsiConsole.MarkupLine("[yellow]interrupted[/]");
            return ProblemDetailsParser.ExitSigInt;
        }
        catch (AgentControlPlaneException ex)
        {
            return ProblemDetailsParser.HandleAndExitCode(ex, AnsiConsole.Console);
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    /// <summary>Parse a comma-separated event-kind list into a set. Returns null when the input is null/empty.</summary>
    internal static HashSet<string>? ParseKindFilter(string? only)
    {
        if (string.IsNullOrWhiteSpace(only))
        {
            return null;
        }
        var kinds = only
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0);
        return new HashSet<string>(kinds, StringComparer.OrdinalIgnoreCase);
    }
}
