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

        [Description("Show stored node executions for this run id instead of starting a new live stream.")]
        [CommandOption("--from-run-id")]
        public string? FromRunId { get; init; }

        [Description("With --from-run-id: show only this node id in full detail.")]
        [CommandOption("--node")]
        public string? NodeId { get; init; }

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

        if (!string.IsNullOrWhiteSpace(settings.FromRunId))
        {
            try
            {
                return await ShowHistoricalRunAsync(client, settings, cancellationToken);
            }
            catch (AgentControlPlaneException ex)
            {
                return ProblemDetailsParser.HandleAndExitCode(ex, AnsiConsole.Console);
            }
        }

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

    private static async Task<int> ShowHistoricalRunAsync(IAgentControlPlaneClient client, Settings settings, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(settings.NodeId))
        {
            var node = await client.GetRunNodeAsync(settings.GraphId, settings.FromRunId!, settings.NodeId, ct);
            if (node is null)
            {
                AnsiConsole.MarkupLine($"[red]error[/] node '{settings.NodeId}' not found in run '{settings.FromRunId}'");
                return ProblemDetailsParser.ExitApiError;
            }

            AnsiConsole.MarkupLine($"[bold]{node.NodeId}[/] ({node.NodeKind})  {node.Status}");
            AnsiConsole.MarkupLine($"  tokens: {node.InputTokens} in / {node.OutputTokens} out" +
                (node.DurationMs.HasValue ? $"  ({node.DurationMs}ms)" : string.Empty));
            if (node.EdgesTaken is { Count: > 0 } edges)
                AnsiConsole.MarkupLine($"  edges: {string.Join(", ", edges)}");
            if (node.Error is not null)
                AnsiConsole.MarkupLine($"  [red]error:[/] {node.Error}");
            if (node.InputText is not null)
            {
                AnsiConsole.MarkupLine("[dim]── input ──────────────────────────────[/]");
                AnsiConsole.WriteLine(node.InputText);
            }
            if (node.OutputText is not null)
            {
                AnsiConsole.MarkupLine("[dim]── output ─────────────────────────────[/]");
                AnsiConsole.WriteLine(node.OutputText);
            }
            return ProblemDetailsParser.ExitSuccess;
        }

        var nodes = await client.GetRunNodesAsync(settings.GraphId, settings.FromRunId!, ct);
        if (nodes.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey](no nodes recorded for this run)[/]");
            return ProblemDetailsParser.ExitSuccess;
        }

        AnsiConsole.MarkupLine($"[bold]run {settings.FromRunId}[/]  {nodes.Count} nodes");
        foreach (var n in nodes)
        {
            AnsiConsole.MarkupLine($"  [cyan]{n.NodeId}[/] ({n.NodeKind})  {n.Status}" +
                (n.DurationMs.HasValue ? $"  {n.DurationMs}ms" : string.Empty) +
                (n.InputTokens + n.OutputTokens > 0 ? $"  {n.InputTokens}+{n.OutputTokens} tok" : string.Empty));
        }
        return ProblemDetailsParser.ExitSuccess;
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
