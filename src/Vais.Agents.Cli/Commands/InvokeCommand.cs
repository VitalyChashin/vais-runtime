// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Vais.Agents.Control.Http;

namespace Vais.Agents.Cli.Commands;

/// <summary>
/// Sends a user message to an agent. Default path = unary
/// <c>InvokeAsync</c> → print assistant text. With <c>--stream</c>
/// routes to <c>InvokeStreamEventsAsync</c> and renders SSE events
/// inline via <see cref="EventRenderer"/>. Ctrl-C during a streamed
/// invoke returns exit <c>130</c>.
/// </summary>
internal sealed class InvokeCommand : AsyncCommand<InvokeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Agent id to invoke.")]
        [CommandArgument(0, "<id>")]
        public required string AgentId { get; init; }

        [Description("User message to send. Prefix with '@' to read from a file (e.g. --text @prompt.txt).")]
        [CommandOption("--text")]
        public required string Text { get; init; }

        [Description("Session id to thread the invocation through (stable across multi-turn conversations).")]
        [CommandOption("--session")]
        public string? SessionId { get; init; }

        [Description("Version to target.")]
        [CommandOption("--version")]
        public string? Version { get; init; }

        [Description("Idempotency-Key attached to the outbound call.")]
        [CommandOption("--idempotency-key")]
        public string? IdempotencyKey { get; init; }

        [Description("Stream events as Server-Sent Events instead of a unary invocation.")]
        [CommandOption("--stream")]
        public bool Stream { get; init; }

        [Description("Output format: text | json. Default: text.")]
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
        string? text;
        try
        {
            text = ArgumentFileReader.Resolve(settings.Text);
        }
        catch (FileNotFoundException ex)
        {
            AnsiConsole.MarkupLine($"[red]error[/] {ex.Message}");
            return ProblemDetailsParser.ExitUsageError;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            AnsiConsole.MarkupLine("[red]error[/] --text is required and must not be empty");
            return ProblemDetailsParser.ExitUsageError;
        }

        var config = VaisConfigFile.LoadOrDefault();
        var client = ClientFactory.Create(config, settings.Context, settings.Token);
        var request = new AgentInvocationRequest(Text: text, SessionId: settings.SessionId);

        try
        {
            if (!settings.Stream)
            {
                var result = await client.InvokeAsync(settings.AgentId, request, settings.Version, settings.IdempotencyKey, cancellationToken);
                var format = OutputFormatter.Parse(settings.Output, OutputFormat.Table); // Table fallback triggers text path below
                if (format == OutputFormat.Json)
                {
                    OutputFormatter.WriteJson(result, AnsiConsole.Console);
                }
                else
                {
                    AnsiConsole.Console.WriteLine(result.Text);
                }
                return ProblemDetailsParser.ExitSuccess;
            }

            // Streaming path — coloured SSE attach; Ctrl-C handled by linked CTS.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            ConsoleCancelEventHandler onCancel = (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };
            Console.CancelKeyPress += onCancel;
            try
            {
                var renderer = new EventRenderer(AnsiConsole.Console);
                await foreach (var evt in client.InvokeStreamEventsAsync(settings.AgentId, request, settings.Version, settings.IdempotencyKey, cts.Token))
                {
                    renderer.Render(evt);
                }
                renderer.FlushAccumulated();
                return ProblemDetailsParser.ExitSuccess;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                AnsiConsole.MarkupLine("[yellow]interrupted[/]");
                return ProblemDetailsParser.ExitSigInt;
            }
            finally
            {
                Console.CancelKeyPress -= onCancel;
            }
        }
        catch (AgentControlPlaneException ex)
        {
            return ProblemDetailsParser.HandleAndExitCode(ex, AnsiConsole.Console);
        }
    }
}
