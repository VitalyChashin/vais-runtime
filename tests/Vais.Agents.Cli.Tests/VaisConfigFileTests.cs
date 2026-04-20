// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Cli;
using Xunit;

namespace Vais.Agents.Cli.Tests;

public sealed class VaisConfigFileTests : IDisposable
{
    private readonly string _tempPath;

    public VaisConfigFileTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), $"vais-cli-test-{Guid.NewGuid():N}.yaml");
    }

    public void Dispose()
    {
        if (File.Exists(_tempPath))
        {
            File.Delete(_tempPath);
        }
    }

    [Fact]
    public void LoadOrDefault_MissingFile_ReturnsEmptyConfig()
    {
        var config = VaisConfigFile.LoadOrDefault(_tempPath);

        config.Should().NotBeNull();
        config.ApiVersion.Should().Be("vais.io/v1");
        config.Kind.Should().Be("Config");
        config.CurrentContext.Should().BeNull();
        config.Clusters.Should().BeEmpty();
        config.Users.Should().BeEmpty();
        config.Contexts.Should().BeEmpty();
    }

    [Fact]
    public void RoundTrip_FullConfig_PreservesAllFields()
    {
        var original = new VaisCliConfig
        {
            ApiVersion = "vais.io/v1",
            Kind = "Config",
            CurrentContext = "default",
            Clusters =
            {
                new VaisCluster { Name = "local", Server = "http://localhost:5080" },
                new VaisCluster { Name = "prod", Server = "https://vais.example.invalid", InsecureSkipTlsVerify = false },
            },
            Users =
            {
                new VaisUser { Name = "dev", Token = "dev-token" },
                new VaisUser { Name = "prod", TokenFile = "/var/run/secrets/vais/token" },
            },
            Contexts =
            {
                new VaisContext { Name = "default", Cluster = "local", User = "dev" },
                new VaisContext { Name = "production", Cluster = "prod", User = "prod" },
            },
        };

        VaisConfigFile.Save(original, _tempPath);
        var roundTripped = VaisConfigFile.LoadOrDefault(_tempPath);

        roundTripped.CurrentContext.Should().Be("default");
        roundTripped.Clusters.Should().HaveCount(2);
        roundTripped.Clusters[1].Server.Should().Be("https://vais.example.invalid");
        roundTripped.Users.Should().HaveCount(2);
        roundTripped.Users[0].Token.Should().Be("dev-token");
        roundTripped.Users[1].TokenFile.Should().Be("/var/run/secrets/vais/token");
        roundTripped.Contexts.Should().HaveCount(2);
        roundTripped.Contexts[0].Cluster.Should().Be("local");
    }

    [Fact]
    public void ResolveConfigPath_EnvOverride_WinsOverUserProfile()
    {
        var overridePath = Path.Combine(Path.GetTempPath(), "vais-override.yaml");
        Environment.SetEnvironmentVariable(VaisConfigFile.PathEnvVar, overridePath);
        try
        {
            VaisConfigFile.ResolveConfigPath().Should().Be(overridePath);
        }
        finally
        {
            Environment.SetEnvironmentVariable(VaisConfigFile.PathEnvVar, null);
        }
    }

    [Fact]
    public void ResolveConfigPath_NoEnv_UsesUserProfile()
    {
        Environment.SetEnvironmentVariable(VaisConfigFile.PathEnvVar, null);
        var resolved = VaisConfigFile.ResolveConfigPath();

        resolved.Should().EndWith(Path.Combine(".vais", "config.yaml"));
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        resolved.Should().StartWith(home);
    }

    [Fact]
    public void FindContext_Missing_ReturnsNull()
    {
        var config = new VaisCliConfig();
        VaisConfigFile.FindContext(config, "absent").Should().BeNull();
    }
}
