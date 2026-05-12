// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.ComponentModel;
using System.Text.Json;
using Spectre.Console.Cli;
using Vais.Agents.Control.Http;

namespace Vais.Agents.Cli.Commands.Diagnose;

/// <summary>
/// Fetches recent OTel spans from the runtime's in-process circular buffer.
/// Output is NDJSON (one span per line) — pipe to <c>jq</c> for filtering.
/// </summary>
internal sealed class DiagnoseSpansCommand : AsyncCommand<DiagnoseSpansCommand.Settings>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public sealed class Settings : CommandSettings
    {
        [Description("Maximum number of spans to return (default 50, max 1000).")]
        [CommandOption("--tail")]
        public int Tail { get; init; } = 50;

        [Description("Filter spans by ActivitySource name (case-insensitive).")]
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
        var config = VaisConfigFile.LoadOrDefault();
        var client = ClientFactory.Create(config, settings.Context, settings.Token);

        try
        {
            var limit = Math.Clamp(settings.Tail, 1, 1000);
            var response = await client.GetDiagSpansAsync(settings.Source, limit, cancellationToken);

            foreach (var span in response.Items)
                Console.WriteLine(JsonSerializer.Serialize(span, JsonOptions));

            return ProblemDetailsParser.ExitSuccess;
        }
        catch (AgentControlPlaneException ex)
        {
            return ProblemDetailsParser.HandleAndExitCode(ex, Spectre.Console.AnsiConsole.Console);
        }
    }
}
