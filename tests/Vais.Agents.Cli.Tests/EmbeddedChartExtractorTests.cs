// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Cli.Commands;
using Xunit;

namespace Vais.Agents.Cli.Tests;

/// <summary>
/// Tests for <see cref="EmbeddedChartExtractor"/> path conversion and extraction logic.
/// </summary>
public sealed class EmbeddedChartExtractorTests
{
    [Fact]
    public void ResourceNameToRelativePath_RootLevelYaml_Unchanged()
    {
        EmbeddedChartExtractor.ResourceNameToRelativePath("Chart.yaml").Should().Be("Chart.yaml");
        EmbeddedChartExtractor.ResourceNameToRelativePath("values.yaml").Should().Be("values.yaml");
    }

    [Fact]
    public void ResourceNameToRelativePath_NestedResource_SeparatesOnDirectorySeparator()
    {
        var sep = Path.DirectorySeparatorChar;
        EmbeddedChartExtractor.ResourceNameToRelativePath("templates.deployment.yaml")
            .Should().Be($"templates{sep}deployment.yaml");
        EmbeddedChartExtractor.ResourceNameToRelativePath("templates.service.yaml")
            .Should().Be($"templates{sep}service.yaml");
    }

    [Fact]
    public void ExtractToTemp_CreatesDirectoryWithExpectedChartFiles()
    {
        var tempDir = EmbeddedChartExtractor.ExtractToTemp();
        try
        {
            Directory.Exists(tempDir).Should().BeTrue();
            File.Exists(Path.Combine(tempDir, "Chart.yaml")).Should().BeTrue();
            File.Exists(Path.Combine(tempDir, "values.yaml")).Should().BeTrue();
            File.Exists(Path.Combine(tempDir, "templates", "deployment.yaml")).Should().BeTrue();
            File.Exists(Path.Combine(tempDir, "templates", "service.yaml")).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ExtractToTemp_EachCallProducesDistinctDirectory()
    {
        var dir1 = EmbeddedChartExtractor.ExtractToTemp();
        var dir2 = EmbeddedChartExtractor.ExtractToTemp();
        try
        {
            dir1.Should().NotBe(dir2);
        }
        finally
        {
            Directory.Delete(dir1, recursive: true);
            Directory.Delete(dir2, recursive: true);
        }
    }
}
