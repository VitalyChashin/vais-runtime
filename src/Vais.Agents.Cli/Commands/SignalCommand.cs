// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.ComponentModel;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Cli;
using Vais.Agents.Control.Http;

namespace Vais.Agents.Cli.Commands;

/// <summary>
/// Sends an <see cref="AgentSignal"/> to an in-flight run —
/// "resume a waiting run with data" primitive. Payload is arbitrary
/// JSON: inline (e.g. <c>--payload '{"ok":true}'</c>) or via <c>@file.json</c>
/// for larger payloads.
/// </summary>
internal sealed class SignalCommand : AsyncCommand<SignalCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Agent id the signal targets.")]
        [CommandArgument(0, "<id>")]
        public required string AgentId { get; init; }

        [Description("Signal kind — consumer-chosen tag (e.g. 'resume', 'cancel-pending', 'reload-config').")]
        [CommandOption("--kind")]
        public required string Kind { get; init; }

        [Description("JSON payload. Inline (e.g. '{\"ok\":true}') or '@path.json' to read from a file.")]
        [CommandOption("--payload")]
        public required string Payload { get; init; }

        [Description("Version to target.")]
        [CommandOption("--version")]
        public string? Version { get; init; }

        [Description("Idempotency-Key attached to the outbound call.")]
        [CommandOption("--idempotency-key")]
        public string? IdempotencyKey { get; init; }

        [Description("Override the active context.")]
        [CommandOption("--context")]
        public string? Context { get; init; }

        [Description("Bearer token override (wins over VAIS_TOKEN + config).")]
        [CommandOption("--token")]
        public string? Token { get; init; }
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        string? payloadJson;
        try
        {
            payloadJson = ArgumentFileReader.Resolve(settings.Payload);
        }
        catch (FileNotFoundException ex)
        {
            AnsiConsole.MarkupLine($"[red]error[/] {ex.Message}");
            return ProblemDetailsParser.ExitUsageError;
        }

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            AnsiConsole.MarkupLine("[red]error[/] --payload is required and must not be empty");
            return ProblemDetailsParser.ExitUsageError;
        }

        JsonElement element;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            element = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            AnsiConsole.MarkupLine($"[red]error[/] --payload must be valid JSON: {ex.Message}");
            return ProblemDetailsParser.ExitUsageError;
        }

        var config = VaisConfigFile.LoadOrDefault();
        var client = ClientFactory.Create(config, settings.Context, settings.Token);

        var signal = new AgentSignal(settings.Kind, element);
        try
        {
            await client.SignalAsync(settings.AgentId, signal, settings.Version, settings.IdempotencyKey, cancellationToken);
            AnsiConsole.MarkupLine($"{settings.AgentId} [yellow]signalled[/] (kind: {settings.Kind})");
            return ProblemDetailsParser.ExitSuccess;
        }
        catch (AgentControlPlaneException ex)
        {
            return ProblemDetailsParser.HandleAndExitCode(ex, AnsiConsole.Console);
        }
    }
}
