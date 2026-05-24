// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Vais.Agents.Control;
using Vais.Agents.Control.InProcess;
using Xunit;

namespace Vais.Agents.Core.Tests;

/// <summary>
/// NB-3: the opt-in JSONL audit sink writes one well-formed JSON object per
/// lifecycle verb — allow, deny, or allowed-but-threw.
/// </summary>
public sealed class JsonlAuditLogTests
{
    private static AuditLogEntry Entry(
        PolicyOperation op = PolicyOperation.EvalSuiteUpsert,
        bool allowed = true,
        string? denyReason = null,
        string? errorType = null) =>
        new(
            At: DateTimeOffset.UtcNow,
            Operation: op,
            AgentId: "suite-1",
            AgentVersion: "1",
            PrincipalId: "alice",
            TenantId: "acme",
            Allowed: allowed,
            DenyReason: denyReason,
            ErrorType: errorType);

    [Fact]
    public async Task Writes_One_Json_Line_Per_Entry()
    {
        var sb = new StringBuilder();
        await using var writer = new StringWriter(sb);
        var log = new JsonlAuditLog(writer);

        await log.AppendAsync(Entry(allowed: true));
        await log.AppendAsync(Entry(PolicyOperation.ExtensionUpdate, allowed: false, denyReason: "policy denied"));
        await log.AppendAsync(Entry(allowed: true, errorType: "InvalidOperationException"));

        var lines = sb.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(3);

        foreach (var line in lines)
        {
            var act = () => JsonDocument.Parse(line);
            act.Should().NotThrow("each audit line must be valid JSON");
        }
    }

    [Fact]
    public async Task Serializes_Fields_Including_Operation_As_String()
    {
        var sb = new StringBuilder();
        await using var writer = new StringWriter(sb);
        var log = new JsonlAuditLog(writer);

        await log.AppendAsync(Entry(PolicyOperation.EvalSuiteUpsert, allowed: false, denyReason: "nope"));

        using var doc = JsonDocument.Parse(sb.ToString().Trim());
        var root = doc.RootElement;
        // Operation serializes as a readable string, not its integer value.
        root.GetProperty("operation").GetString().Should().Be("EvalSuiteUpsert");
        root.GetProperty("allowed").GetBoolean().Should().BeFalse();
        root.GetProperty("denyReason").GetString().Should().Be("nope");
        root.GetProperty("principalId").GetString().Should().Be("alice");
    }

    [Fact]
    public async Task Append_Never_Throws_Into_Caller_When_Writer_Faults()
    {
        var log = new JsonlAuditLog(new ThrowingWriter());
        var act = async () => await log.AppendAsync(Entry());
        await act.Should().NotThrowAsync("audit-write failures must not break the verb");
    }

    private sealed class ThrowingWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) => throw new IOException("disk full");
        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
            => throw new IOException("disk full");
    }
}
