// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using Vais.Agents.Control;
using Vais.Agents.Control.InProcess;
using Xunit;

namespace Vais.Agents.Core.Tests;

public sealed class LoggerAuditLogTests
{
    [Fact]
    public async Task Writes_Every_Field_To_Logger_As_Structured_Entry()
    {
        var capture = new CapturingLogger();
        var audit = new LoggerAuditLog(capture);
        await audit.AppendAsync(new AuditLogEntry(
            At: DateTimeOffset.UtcNow,
            Operation: PolicyOperation.Create,
            AgentId: "support",
            AgentVersion: "1.0",
            PrincipalId: "alice",
            TenantId: "acme",
            Allowed: true,
            DenyReason: null,
            ErrorType: null));

        capture.Messages.Should().ContainSingle()
            .Which.Should().Contain("Create")
            .And.Contain("support")
            .And.Contain("alice");
    }

    [Fact]
    public async Task Denied_Entries_Log_At_Warning_Level()
    {
        var capture = new CapturingLogger();
        var audit = new LoggerAuditLog(capture);
        await audit.AppendAsync(new AuditLogEntry(
            At: DateTimeOffset.UtcNow,
            Operation: PolicyOperation.Invoke,
            AgentId: "x", AgentVersion: "1",
            PrincipalId: "bob", TenantId: null,
            Allowed: false, DenyReason: "quota exceeded", ErrorType: null));

        capture.Levels.Should().ContainSingle().Which.Should().Be(LogLevel.Warning);
    }

    [Fact]
    public async Task Logger_Exception_Does_Not_Propagate_To_Caller()
    {
        var audit = new LoggerAuditLog(new ThrowingLogger());
        // Must not throw — swallow discipline is part of the contract.
        await audit.AppendAsync(new AuditLogEntry(
            DateTimeOffset.UtcNow, PolicyOperation.Query, "x", "1", "p", null, true, null, null));
    }

    private sealed class CapturingLogger : ILogger<LoggerAuditLog>
    {
        public List<string> Messages { get; } = new();
        public List<LogLevel> Levels { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Levels.Add(logLevel);
            Messages.Add(formatter(state, exception));
        }
    }

    private sealed class ThrowingLogger : ILogger<LoggerAuditLog>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => throw new InvalidOperationException("boom");
    }
}

public sealed class SecretResolverTests
{
    [Fact]
    public async Task Environment_Resolver_Reads_Env_Var()
    {
        const string key = "VAIS_AGENTS_TEST_SECRET";
        Environment.SetEnvironmentVariable(key, "super-secret");
        try
        {
            var resolver = new EnvironmentSecretResolver();
            var value = await resolver.ResolveAsync($"secret://env/{key}");
            value.Should().Be("super-secret");
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    [Fact]
    public async Task Environment_Resolver_Throws_SecretNotFound_When_Var_Missing()
    {
        var resolver = new EnvironmentSecretResolver();
        await FluentActions.Invoking(async () => await resolver.ResolveAsync("secret://env/NOPE_DEFINITELY_NOT_SET_12345"))
            .Should().ThrowAsync<SecretNotFoundException>();
    }

    [Fact]
    public async Task File_Resolver_Reads_And_Trims_Trailing_Whitespace()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "top-secret\n");
            var resolver = new FileSecretResolver();
            var value = await resolver.ResolveAsync($"secret://file/{path.Replace('\\', '/')}");
            value.Should().Be("top-secret");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task File_Resolver_Throws_SecretNotFound_When_File_Missing()
    {
        var resolver = new FileSecretResolver();
        await FluentActions.Invoking(async () => await resolver.ResolveAsync("secret://file/nonexistent-path-abc-123"))
            .Should().ThrowAsync<SecretNotFoundException>();
    }

    [Fact]
    public async Task Composite_Resolver_Dispatches_By_Scheme()
    {
        const string key = "VAIS_AGENTS_COMPOSITE_TEST";
        Environment.SetEnvironmentVariable(key, "env-value");
        try
        {
            var composite = CompositeSecretResolver.CreateDefault();
            (await composite.ResolveAsync($"secret://env/{key}")).Should().Be("env-value");
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    [Fact]
    public async Task Composite_Resolver_Rejects_Unknown_Scheme()
    {
        var composite = CompositeSecretResolver.CreateDefault();
        await FluentActions.Invoking(async () => await composite.ResolveAsync("secret://keyvault/nope"))
            .Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task Resolver_Rejects_Non_Secret_URI()
    {
        var resolver = new EnvironmentSecretResolver();
        await FluentActions.Invoking(async () => await resolver.ResolveAsync("http://not-a-secret/path"))
            .Should().ThrowAsync<NotSupportedException>();
    }
}
