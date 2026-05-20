// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using Vais.Agents.Runtime.Plugins.Container.StructuredLog;
using Xunit;

namespace Vais.Agents.Runtime.Plugins.Container.Tests.StructuredLog;

public sealed class StructuredLogForwarderTests
{
    [Theory]
    [InlineData("INFO",     LogLevel.Information)]
    [InlineData("info",     LogLevel.Information)]
    [InlineData("WARN",     LogLevel.Warning)]
    [InlineData("WARNING",  LogLevel.Warning)]
    [InlineData("DEBUG",    LogLevel.Debug)]
    [InlineData("TRACE",    LogLevel.Trace)]
    [InlineData("ERROR",    LogLevel.Error)]
    [InlineData("CRITICAL", LogLevel.Critical)]
    [InlineData("FATAL",    LogLevel.Critical)]
    [InlineData(null,       LogLevel.Information)]
    [InlineData("UNKNOWN",  LogLevel.Information)]
    public void ParseLevel_MapsCorrectly(string? input, LogLevel expected)
    {
        StructuredLogForwarder.ParseLevel(input).Should().Be(expected);
    }

    [Fact]
    public void Forward_WithFields_DoesNotThrow()
    {
        using var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Trace));
        var logger = loggerFactory.CreateLogger("test");
        var forwarder = new StructuredLogForwarder(logger);

        var record = new PluginLogRecord
        {
            Timestamp = "2026-05-20T12:00:00Z",
            Severity  = "INFO",
            Message   = "processed 42 records",
            Fields    = new Dictionary<string, object?> { ["component"] = "loader", ["count"] = 42 },
        };

        var act = () => forwarder.Forward(record, "my-plugin", "plugin", extensionId: null);
        act.Should().NotThrow();
    }

    [Fact]
    public void Forward_WithoutFields_DoesNotThrow()
    {
        using var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Trace));
        var logger = loggerFactory.CreateLogger("test");
        var forwarder = new StructuredLogForwarder(logger);

        var record = new PluginLogRecord { Severity = "WARN", Message = "slow query" };
        var act = () => forwarder.Forward(record, "my-plugin", "plugin", extensionId: null);
        act.Should().NotThrow();
    }

    [Fact]
    public void Forward_ExtensionSource_DoesNotThrow()
    {
        using var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Trace));
        var logger = loggerFactory.CreateLogger("test");
        var forwarder = new StructuredLogForwarder(logger);

        var record = new PluginLogRecord { Severity = "DEBUG", Message = "handler invoked" };
        var act = () => forwarder.Forward(record, "vais-ext-log", "extension", extensionId: "vais-ext-log");
        act.Should().NotThrow();
    }
}
