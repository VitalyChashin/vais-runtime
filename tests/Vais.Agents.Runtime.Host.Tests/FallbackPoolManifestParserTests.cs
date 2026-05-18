// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Vais.Agents.Runtime.Host.Tests;

/// <summary>
/// Guards the wire-through contract of the <c>Fallback</c> middleware's
/// <c>pool:</c> manifest entries. Each entry must materialise as a full
/// <see cref="ModelSpec"/> — operators rely on this to vary endpoint
/// (<c>baseUrlRef</c>) and decoding params per pool entry, e.g. an Azure-OpenAI
/// fallback after a primary OpenAI provider, or a self-hosted vLLM endpoint as
/// the last line of defence.
/// </summary>
public sealed class FallbackPoolManifestParserTests
{
    [Fact]
    public void ParseEntry_Forwards_All_ModelSpec_Fields()
    {
        const string json = """
        {
          "provider": "openai",
          "id": "gpt-4o",
          "apiKeyRef": "secret://env/OPENAI_API_KEY",
          "baseUrlRef": "secret://env/CUSTOM_ENDPOINT",
          "temperature": 0.4,
          "topP": 0.9,
          "maxTokens": 1024,
          "responseFormat": "json"
        }
        """;
        var entry = JsonDocument.Parse(json).RootElement;

        var spec = FallbackPoolManifestParser.ParseEntry(entry);

        spec.Provider.Should().Be("openai");
        spec.Id.Should().Be("gpt-4o");
        spec.ApiKeyRef.Should().Be("secret://env/OPENAI_API_KEY");
        spec.BaseUrlRef.Should().Be("secret://env/CUSTOM_ENDPOINT");
        spec.Temperature.Should().Be(0.4);
        spec.TopP.Should().Be(0.9);
        spec.MaxTokens.Should().Be(1024);
        spec.ResponseFormat.Should().Be("json");
    }

    [Fact]
    public void ParseEntry_Optional_Fields_Default_To_Null()
    {
        const string json = """
        {
          "provider": "anthropic",
          "id": "claude-3-haiku-20240307",
          "apiKeyRef": "secret://env/ANTHROPIC_API_KEY"
        }
        """;
        var entry = JsonDocument.Parse(json).RootElement;

        var spec = FallbackPoolManifestParser.ParseEntry(entry);

        spec.BaseUrlRef.Should().BeNull();
        spec.Temperature.Should().BeNull();
        spec.TopP.Should().BeNull();
        spec.MaxTokens.Should().BeNull();
        spec.ResponseFormat.Should().BeNull();
    }

    [Fact]
    public void ParsePool_Materialises_Each_Entry_In_Order()
    {
        const string json = """
        {
          "pool": [
            { "provider": "openai",       "id": "gpt-4o",                  "apiKeyRef": "secret://env/OPENAI_API_KEY" },
            { "provider": "openai",       "id": "llama-3-70b",             "apiKeyRef": "secret://env/VLLM_API_KEY", "baseUrlRef": "secret://env/VLLM_ENDPOINT" },
            { "provider": "azure-openai", "id": "gpt-4o-deployment",       "apiKeyRef": "secret://env/AZURE_KEY",    "baseUrlRef": "secret://env/AZURE_ENDPOINT" }
          ]
        }
        """;
        var paramsEl = JsonDocument.Parse(json).RootElement;

        var specs = FallbackPoolManifestParser.ParsePool(paramsEl).ToArray();

        specs.Should().HaveCount(3);
        specs[0].Provider.Should().Be("openai");
        specs[0].BaseUrlRef.Should().BeNull(because: "no override on the primary entry");
        specs[1].BaseUrlRef.Should().Be("secret://env/VLLM_ENDPOINT",
            because: "vLLM fallback entry must carry its custom endpoint through to the factory");
        specs[2].Provider.Should().Be("azure-openai");
        specs[2].BaseUrlRef.Should().Be("secret://env/AZURE_ENDPOINT",
            because: "azure-openai requires baseUrlRef and the parser must forward it");
    }

    [Fact]
    public void ParsePool_Returns_Empty_When_Params_Are_Null()
    {
        FallbackPoolManifestParser.ParsePool(null).Should().BeEmpty();
    }

    [Fact]
    public void ParsePool_Returns_Empty_When_Pool_Key_Is_Missing()
    {
        var paramsEl = JsonDocument.Parse("{\"otherKey\": []}").RootElement;
        FallbackPoolManifestParser.ParsePool(paramsEl).Should().BeEmpty();
    }
}
