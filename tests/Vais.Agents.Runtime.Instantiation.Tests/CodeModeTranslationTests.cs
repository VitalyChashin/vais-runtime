// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Vais.Agents.Runtime.Instantiation.Tests;

/// <summary>
/// CM-4 — when <c>spec.codeMode.enabled</c>, the translator substitutes a single <c>run_code</c>
/// tool for the LLM-facing registry (the gateway path is unaffected), and a code-mode manifest
/// fails to translate when no <see cref="ICodeModeToolFactory"/> is registered.
/// </summary>
public sealed class CodeModeTranslationTests
{
    private static AgentManifest CodeModeManifest() =>
        new("coder", "1.0", new AgentHandlerRef("declarative"), [new ProtocolBinding("Http")], [new ToolRef("crm", "static:crm")])
        {
            Model = new ModelSpec("openai", "gpt-4o-mini"),
            CodeMode = new CodeModeSpec { Enabled = true },
        };

    [Fact]
    public async Task CodeModeAgent_ExposesOnlyRunCodeToTheLlm()
    {
        var options = await new TranslatorFixture()
            .WithManifest(CodeModeManifest())
            .WithProvider("openai")
            .WithStaticTool("crm", new StubTool("crm"))
            .WithCodeModeToolFactory(new StubCodeModeFactory())
            .Translator.TranslateAsync("coder");

        options.ToolRegistry.Should().NotBeNull();
        options.ToolRegistry!.Tools.Should().ContainSingle().Which.Name.Should().Be("run_code");
    }

    [Fact]
    public async Task CodeModeEnabled_WithoutFactory_ThrowsClearError()
    {
        var act = async () => await new TranslatorFixture()
            .WithManifest(CodeModeManifest())
            .WithProvider("openai")
            .WithStaticTool("crm", new StubTool("crm"))
            .Translator.TranslateAsync("coder");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private sealed class StubCodeModeFactory : ICodeModeToolFactory
    {
        public ITool Create(string agentId, CodeModeSpec spec, IReadOnlyList<ITool> tools) => new StubTool("run_code");
    }

    private sealed class StubTool(string name) : ITool
    {
        public string Name => name;
        public string Description => string.Empty;
        public JsonElement ParametersSchema => default;
        public Task<string> InvokeAsync(JsonElement arguments, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Empty);
    }
}
