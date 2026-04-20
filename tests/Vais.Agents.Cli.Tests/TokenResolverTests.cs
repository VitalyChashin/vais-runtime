// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Cli;
using Xunit;

namespace Vais.Agents.Cli.Tests;

public sealed class TokenResolverTests
{
    [Fact]
    public void Resolve_Flag_WinsOverEnvAndContext()
    {
        Environment.SetEnvironmentVariable(TokenResolver.TokenEnvVar, "env-token");
        try
        {
            var result = TokenResolver.Resolve(
                tokenFlag: "flag-token",
                contextUser: new VaisUser { Name = "u", Token = "ctx-token" });
            result.Should().Be("flag-token");
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenResolver.TokenEnvVar, null);
        }
    }

    [Fact]
    public void Resolve_Env_WinsOverContext()
    {
        Environment.SetEnvironmentVariable(TokenResolver.TokenEnvVar, "env-token");
        try
        {
            var result = TokenResolver.Resolve(
                tokenFlag: null,
                contextUser: new VaisUser { Name = "u", Token = "ctx-token" });
            result.Should().Be("env-token");
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenResolver.TokenEnvVar, null);
        }
    }

    [Fact]
    public void Resolve_ContextUser_UsedWhenNoFlagNoEnv()
    {
        Environment.SetEnvironmentVariable(TokenResolver.TokenEnvVar, null);
        var result = TokenResolver.Resolve(
            tokenFlag: null,
            contextUser: new VaisUser { Name = "u", Token = "ctx-token" });
        result.Should().Be("ctx-token");
    }

    [Fact]
    public void Resolve_TokenFile_ReadFromDisk()
    {
        Environment.SetEnvironmentVariable(TokenResolver.TokenEnvVar, null);
        var tempFile = Path.Combine(Path.GetTempPath(), $"vais-tok-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(tempFile, "file-token\n");
            var result = TokenResolver.Resolve(
                tokenFlag: null,
                contextUser: new VaisUser { Name = "u", TokenFile = tempFile });
            result.Should().Be("file-token");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void Resolve_NoSources_ReturnsNull()
    {
        Environment.SetEnvironmentVariable(TokenResolver.TokenEnvVar, null);
        TokenResolver.Resolve(tokenFlag: null, contextUser: null).Should().BeNull();
    }
}
