// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.ComponentModel;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Cli;
using Vais.Agents.Control.Http;

namespace Vais.Agents.Cli.Commands;

/// <summary>
/// Invokes a graph run — or resumes an interrupted one. Default path: unary
/// <c>InvokeGraphAsync</c> returning final state. With <c>--stream</c> routes to
/// <c>InvokeGraphStreamAsync</c>. With <c>--resume-from &lt;interruptId&gt;</c>
/// routes to <c>ResumeGraphAsync</c> (or <c>ResumeGraphStreamAsync</c> when combined
/// with <c>--stream</c>).
/// </summary>
internal sealed class InvokeGraphCommand : AsyncCommand<InvokeGraphCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Graph id to invoke.")]
        [CommandArgument(0, "<id>")]
        public required string GraphId { get; init; }

        [Description("Initial state as JSON (inline or @file). Defaults to empty state '{}' when omitted.")]
        [CommandOption("--initial-state")]
        public string? InitialState { get; init; }

        [Description("Version to target.")]
        [CommandOption("--version")]
        public string? Version { get; init; }

        [Description("Caller-supplied run identifier (optional; random when omitted).")]
        [CommandOption("--run-id")]
        public string? RunId { get; init; }

        [Description("Stream graph events as Server-Sent Events instead of a unary invocation.")]
        [CommandOption("--stream")]
        public bool Stream { get; init; }

        [Description("Resume an interrupted run. Supply the interruptId from the prior invocation result.")]
        [CommandOption("--resume-from")]
        public string? ResumeFrom { get; init; }

        [Description("Resume payload as JSON (inline or @file). Merged into graph state at the resume node.")]
        [CommandOption("--resume-payload")]
        public string? ResumePayload { get; init; }

        [Description("Idempotency-Key attached to the outbound call.")]
        [CommandOption("--idempotency-key")]
        public string? IdempotencyKey { get; init; }

        [Description("Output format: json | state. 'state' serialises the final state bag as indented JSON. Default: last assistant text (plain).")]
        [CommandOption("-o|--output")]
        public string? Output { get; init; }

        [Description("Override the active context.")]
        [CommandOption("--context")]
        public string? Context { get; init; }

        [Description("Bearer token override (wins over VAIS_TOKEN + config).")]
        [CommandOption("--token")]
        public string? Token { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        IDictionary<string, JsonElement> initialState;
        try
        {
            var raw = ArgumentFileReader.Resolve(settings.InitialState);
            initialState = ParseStateBag(raw);
        }
        catch (FileNotFoundException ex)
        {
            AnsiConsole.MarkupLine($"[red]error[/] {ex.Message}");
            return ProblemDetailsParser.ExitUsageError;
        }
        catch (JsonException ex)
        {
            AnsiConsole.MarkupLine($"[red]error[/] --initial-state must be valid JSON: {ex.Message}");
            return ProblemDetailsParser.ExitUsageError;
        }

        var config = VaisConfigFile.LoadOrDefault();
        var client = ClientFactory.Create(config, settings.Context, settings.Token);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cts.Cancel(); };
        Console.CancelKeyPress += onCancel;

        try
        {
            // Resume path
            if (!string.IsNullOrWhiteSpace(settings.ResumeFrom))
            {
                if (string.IsNullOrWhiteSpace(settings.RunId))
                {
                    AnsiConsole.MarkupLine("[red]error[/] --run-id is required with --resume-from");
                    return ProblemDetailsParser.ExitUsageError;
                }

                JsonElement? resumePayload = null;
                if (!string.IsNullOrWhiteSpace(settings.ResumePayload))
                {
                    try
                    {
                        var raw = ArgumentFileReader.Resolve(settings.ResumePayload);
                        resumePayload = JsonDocument.Parse(raw!).RootElement;
                    }
                    catch (JsonException ex)
                    {
                        AnsiConsole.MarkupLine($"[red]error[/] --resume-payload must be valid JSON: {ex.Message}");
                        return ProblemDetailsParser.ExitUsageError;
                    }
                }

                var resumeRequest = new GraphResumeRequest(
                    RunId: settings.RunId,
                    InterruptId: settings.ResumeFrom,
                    ResumePayload: resumePayload);

                if (settings.Stream)
                {
                    return await StreamGraphAsync(
                        client.ResumeGraphStreamAsync(settings.GraphId, settings.RunId, resumeRequest, settings.Version, settings.IdempotencyKey, cts.Token),
                        cancellationToken: cts.Token);
                }

                var result = await client.ResumeGraphAsync(settings.GraphId, settings.RunId, resumeRequest, settings.Version, settings.IdempotencyKey, cts.Token);
                return PrintResult(result, settings.Output);
            }

            // Invoke path
            var request = new GraphInvocationRequest(
                InitialState: initialState,
                RunId: settings.RunId);

            if (settings.Stream)
            {
                return await StreamGraphAsync(
                    client.InvokeGraphStreamAsync(settings.GraphId, request, settings.Version, settings.IdempotencyKey, cts.Token),
                    cancellationToken: cts.Token);
            }

            var invokeResult = await client.InvokeGraphAsync(settings.GraphId, request, settings.Version, settings.IdempotencyKey, cts.Token);
            return PrintResult(invokeResult, settings.Output);
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

    private static async Task<int> StreamGraphAsync(IAsyncEnumerable<AgentGraphEvent> events, CancellationToken cancellationToken)
    {
        var renderer = new GraphEventRenderer(AnsiConsole.Console);
        await foreach (var evt in events.WithCancellation(cancellationToken))
        {
            renderer.Render(evt);
        }
        return ProblemDetailsParser.ExitSuccess;
    }

    private static int PrintResult(GraphInvocationResult result, string? output)
    {
        if (string.Equals(output, "state", StringComparison.OrdinalIgnoreCase))
        {
            var json = JsonSerializer.Serialize(result.FinalState, new JsonSerializerOptions { WriteIndented = true });
            AnsiConsole.Console.WriteLine(json);
            return ProblemDetailsParser.ExitSuccess;
        }

        var format = OutputFormatter.Parse(output, OutputFormat.Table);
        if (format == OutputFormat.Json)
        {
            OutputFormatter.WriteJson(result, AnsiConsole.Console);
        }
        else
        {
            if (result.IsComplete)
            {
                if (result.FinalState.TryGetValue("lastAssistantText", out var lat) &&
                    lat.ValueKind == JsonValueKind.String)
                {
                    AnsiConsole.Console.WriteLine(lat.GetString()!);
                }
            }
            else if (result.PendingInterruptId is not null)
            {
                AnsiConsole.MarkupLine($"[yellow]interrupted[/] interruptId={result.PendingInterruptId} node={result.PendingInterruptNodeId} reason={result.PendingInterruptReason ?? "(none)"}");
                AnsiConsole.MarkupLine($"[dim]Resume with: vais invoke-graph <id> --run-id {result.RunId} --resume-from {result.PendingInterruptId}[/]");
            }
        }
        return ProblemDetailsParser.ExitSuccess;
    }

    /// <summary>Parse a JSON object string into a state bag. Null/empty → empty dictionary.</summary>
    internal static IDictionary<string, JsonElement> ParseStateBag(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        }
        var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("State must be a JSON object.");
        }
        return doc.RootElement.EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
    }
}
