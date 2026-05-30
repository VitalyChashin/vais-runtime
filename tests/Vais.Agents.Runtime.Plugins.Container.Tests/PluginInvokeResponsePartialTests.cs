// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using Xunit;

namespace Vais.Agents.Runtime.Plugins.Container.Tests;

/// <summary>
/// Part 1 (honest signals), Unit 7: the plugin <c>isPartial</c>/<c>failureReason</c> contract.
/// Verifies the camelCase wire fields the Python SDK emits (<c>vais_plugin/agent.py:_serialise_response</c>)
/// deserialize onto <see cref="PluginInvokeResponse"/> via <see cref="ContainerJsonOptions.Default"/>
/// (<c>JsonSerializerDefaults.Web</c> — camelCase, case-insensitive read). The shim maps
/// <c>IsPartial</c> to a WARNING-level <c>TurnCompleted</c> so a degraded plugin turn is not silently
/// masked as a clean success.
/// </summary>
public sealed class PluginInvokeResponsePartialTests
{
    [Fact]
    public void Deserializes_Partial_Result_From_CamelCase_Wire()
    {
        // Mirror exactly what vais_plugin/agent.py:_serialise_response emits for a partial result.
        const string wire = """
            {"assistantMessage":"No analysis produced.","isPartial":true,"failureReason":"SGR engine returned no analysis."}
            """;

        var response = JsonSerializer.Deserialize<PluginInvokeResponse>(wire, ContainerJsonOptions.Default);

        Assert.NotNull(response);
        Assert.Equal("No analysis produced.", response!.AssistantMessage);
        Assert.True(response.IsPartial);
        Assert.Equal("SGR engine returned no analysis.", response.FailureReason);
    }

    [Fact]
    public void Normal_Result_Has_IsPartial_False_And_No_Reason()
    {
        // A clean success omits isPartial/failureReason entirely (the Python serializer only
        // writes them when partial) — they must default to false/null, not throw.
        const string wire = """{"assistantMessage":"Here is the analysis."}""";

        var response = JsonSerializer.Deserialize<PluginInvokeResponse>(wire, ContainerJsonOptions.Default);

        Assert.NotNull(response);
        Assert.Equal("Here is the analysis.", response!.AssistantMessage);
        Assert.False(response.IsPartial);
        Assert.Null(response.FailureReason);
    }
}
