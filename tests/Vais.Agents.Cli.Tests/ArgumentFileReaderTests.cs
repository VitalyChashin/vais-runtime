// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Xunit;

namespace Vais.Agents.Cli.Tests;

public sealed class ArgumentFileReaderTests : IDisposable
{
    private readonly string _tempFile;

    public ArgumentFileReaderTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"vais-arg-{Guid.NewGuid():N}.txt");
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile))
        {
            File.Delete(_tempFile);
        }
    }

    [Fact]
    public void Resolve_PlainValue_ReturnsUnchanged()
    {
        ArgumentFileReader.Resolve("hello world").Should().Be("hello world");
    }

    [Fact]
    public void Resolve_NullOrEmpty_PassesThrough()
    {
        ArgumentFileReader.Resolve(null).Should().BeNull();
        ArgumentFileReader.Resolve(string.Empty).Should().BeEmpty();
    }

    [Fact]
    public void Resolve_AtPrefix_ReadsFileContents()
    {
        File.WriteAllText(_tempFile, "contents-from-disk");

        ArgumentFileReader.Resolve($"@{_tempFile}").Should().Be("contents-from-disk");
    }

    [Fact]
    public void Resolve_AtPrefix_MissingFile_Throws()
    {
        FluentActions.Invoking(() => ArgumentFileReader.Resolve("@/does/not/exist"))
            .Should().Throw<FileNotFoundException>();
    }
}
