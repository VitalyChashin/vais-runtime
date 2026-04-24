// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Xunit;

namespace Vais.Agents.Runtime.Plugins.Python.Tests;

public sealed class PluginYamlDeserializerTests
{
    private readonly PluginYamlDeserializer _sut = new();

    [Fact]
    public void Deserialize_FullDocument_MapsAllFields()
    {
        var yaml = """
            apiVersion: vais.agents/v1
            kind: Plugin
            metadata:
              name: research-planner
            spec:
              runtime: python
              entrypoint: src/research_planner/server.py
              python:
                version: "3.13"
                interpreter: .venv/bin/python
              health:
                handshakeTimeoutSeconds: 10
                restartPolicy: exponentialBackoff
            """;

        var doc = _sut.Deserialize(yaml);

        doc.Should().NotBeNull();
        doc!.ApiVersion.Should().Be("vais.agents/v1");
        doc.Kind.Should().Be("Plugin");
        doc.Metadata!.Name.Should().Be("research-planner");
        doc.Spec!.Runtime.Should().Be("python");
        doc.Spec.Entrypoint.Should().Be("src/research_planner/server.py");
        doc.Spec.Python!.Version.Should().Be("3.13");
        doc.Spec.Python.Interpreter.Should().Be(".venv/bin/python");
        doc.Spec.Health!.HandshakeTimeoutSeconds.Should().Be(10);
        doc.Spec.Health.RestartPolicy.Should().Be("exponentialBackoff");
    }

    [Fact]
    public void Deserialize_MissingHealthSection_ReturnsNullHealth()
    {
        var yaml = """
            apiVersion: vais.agents/v1
            kind: Plugin
            metadata:
              name: minimal-plugin
            spec:
              runtime: python
              entrypoint: server.py
            """;

        var doc = _sut.Deserialize(yaml);

        doc!.Spec!.Health.Should().BeNull();
        doc.Spec.Python.Should().BeNull();
    }

    [Fact]
    public void Deserialize_NonPythonRuntime_ReturnsDocWithDifferentRuntime()
    {
        var yaml = """
            apiVersion: vais.agents/v1
            kind: Plugin
            spec:
              runtime: node
            """;

        var doc = _sut.Deserialize(yaml);

        doc!.Spec!.Runtime.Should().Be("node");
    }

    [Fact]
    public void Deserialize_EmptyYaml_ReturnsNull()
    {
        var doc = _sut.Deserialize("");

        doc.Should().BeNull();
    }

    [Fact]
    public void Deserialize_UnknownTopLevelKeys_IgnoresThem()
    {
        var yaml = """
            apiVersion: vais.agents/v1
            unknownField: some-value
            spec:
              runtime: python
              entrypoint: server.py
              unknownSpecField: 42
            """;

        var act = () => _sut.Deserialize(yaml);

        act.Should().NotThrow("IgnoreUnmatchedProperties is configured");
    }

    [Fact]
    public void Deserialize_MalformedYaml_ThrowsYamlException()
    {
        var yaml = "spec: [\n  unclosed";

        var act = () => _sut.Deserialize(yaml);

        act.Should().Throw<YamlDotNet.Core.YamlException>();
    }
}
