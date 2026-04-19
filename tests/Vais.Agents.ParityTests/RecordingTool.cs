// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents.ParityTests;

/// <summary>
/// <see cref="ITool"/> test double: records every call, returns a canned reply.
/// Shared by the SK- and MAF-side parity scenarios so the only thing that differs
/// between them is the adapter plumbing.
/// </summary>
internal sealed class RecordingTool : ITool
{
    private static readonly JsonElement Schema = JsonSerializer.Deserialize<JsonElement>(
        """
        {
          "type": "object",
          "properties": {
            "city": {
              "type": "string",
              "description": "City to look up the weather for."
            }
          },
          "required": ["city"]
        }
        """);

    private readonly string _reply;

    public RecordingTool(string reply = """{"temperature":72,"unit":"F"}""")
    {
        _reply = reply;
    }

    public string Name => "get_weather";
    public string Description => "Return the current weather for a city.";
    public JsonElement ParametersSchema => Schema;

    public List<JsonElement> Invocations { get; } = new();

    public Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        Invocations.Add(arguments.Clone());
        return Task.FromResult(_reply);
    }
}
