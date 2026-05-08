// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Plugin.Sdk;

internal static class SseWriter
{
    internal static string EncodeEvent(SseEvent e) =>
        $"event: {e.Event}\ndata: {JsonSerializer.Serialize(e.Data, PluginJsonOptions.Default)}\n\n";

    internal static string EncodeError(string errorType, string errorMessage) =>
        $"event: error\ndata: {JsonSerializer.Serialize(new { errorType, errorMessage }, PluginJsonOptions.Default)}\n\n";

    internal static string HeartbeatComment => ": heartbeat\n\n";
}
