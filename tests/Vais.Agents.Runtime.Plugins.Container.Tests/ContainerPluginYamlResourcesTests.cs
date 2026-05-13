// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Xunit;

namespace Vais.Agents.Runtime.Plugins.Container.Tests;

public sealed class ContainerPluginYamlResourcesTests
{
    private static readonly ContainerPluginYamlDeserializer _deserializer = new();

    // ── YAML deserialization ──────────────────────────────────────────────

    [Fact]
    public void Deserialize_WithFullResourceBlock_ParsesAllFields()
    {
        var yaml = """
            apiVersion: v0.24
            kind: ContainerPlugin
            metadata:
              name: my-plugin
            spec:
              runtime: container
              image: my-image:1.0
              resources:
                memory: 256Mi
                cpu: "0.5"
                pidsLimit: 128
            """;

        var r = _deserializer.Deserialize(yaml)!.Spec!.Resources;
        r.Should().NotBeNull();
        r!.Memory.Should().Be("256Mi");
        r.Cpu.Should().Be("0.5");
        r.PidsLimit.Should().Be(128);
    }

    [Fact]
    public void Deserialize_WithoutResourceBlock_ResourcesIsNull()
    {
        var yaml = """
            apiVersion: v0.24
            kind: ContainerPlugin
            metadata:
              name: my-plugin
            spec:
              runtime: container
              image: my-image:1.0
            """;

        _deserializer.Deserialize(yaml)!.Spec!.Resources.Should().BeNull();
    }

    [Fact]
    public void Deserialize_PartialResourceBlock_OmittedFieldsAreNull()
    {
        var yaml = """
            apiVersion: v0.24
            kind: ContainerPlugin
            metadata:
              name: my-plugin
            spec:
              runtime: container
              image: my-image:1.0
              resources:
                memory: 512Mi
            """;

        var r = _deserializer.Deserialize(yaml)!.Spec!.Resources;
        r!.Memory.Should().Be("512Mi");
        r.Cpu.Should().BeNull();
        r.PidsLimit.Should().BeNull();
    }

    // ── Resource parser — memory ──────────────────────────────────────────

    [Theory]
    [InlineData("256Mi", 256L * 1024 * 1024)]
    [InlineData("1Gi", 1L * 1024 * 1024 * 1024)]
    [InlineData("512Ki", 512L * 1024)]
    [InlineData("1024", 1024L)]
    [InlineData("500M", 500L * 1_000_000)]
    [InlineData("2G", 2L * 1_000_000_000)]
    public void ParseMemoryBytes_RecognisesAllUnits(string raw, long expectedBytes)
    {
        ContainerPluginResourceParser.ParseMemoryBytes(raw).Should().Be(expectedBytes);
    }

    [Fact]
    public void ParseMemoryBytes_NullInput_ReturnsNull()
    {
        ContainerPluginResourceParser.ParseMemoryBytes(null).Should().BeNull();
    }

    // ── Resource parser — CPU ─────────────────────────────────────────────

    [Theory]
    [InlineData("0.5", 500_000_000L)]
    [InlineData("1.0", 1_000_000_000L)]
    [InlineData("500m", 500_000_000L)]
    [InlineData("250m", 250_000_000L)]
    public void ParseNanoCpus_RecognisesUnits(string raw, long expectedNano)
    {
        ContainerPluginResourceParser.ParseNanoCpus(raw).Should().Be(expectedNano);
    }

    [Fact]
    public void ParseNanoCpus_NullInput_ReturnsNull()
    {
        ContainerPluginResourceParser.ParseNanoCpus(null).Should().BeNull();
    }

    // ── Clamp ─────────────────────────────────────────────────────────────

    [Fact]
    public void Clamp_ValueBelowMax_ReturnsValue()
    {
        ContainerPluginResourceParser.Clamp(100, 200).Should().Be(100);
    }

    [Fact]
    public void Clamp_ValueAboveMax_ReturnsMax()
    {
        ContainerPluginResourceParser.Clamp(300, 200).Should().Be(200);
    }

    [Fact]
    public void Clamp_NullValue_ReturnsNull()
    {
        ContainerPluginResourceParser.Clamp(null, 200).Should().BeNull();
    }
}
