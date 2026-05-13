// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Xunit;

namespace Vais.Agents.Control.Http.Tests;

public sealed class RemoteRuntimeOptionsTests
{
    [Fact]
    public void Validate_ForwardMode_SucceedsWithNoExtraFields()
    {
        var opts = new RemoteRuntimeOptions { IdentityMode = RemoteIdentityMode.Forward };
        var act = () => opts.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_TokenExchange_MissingEndpoint_Throws()
    {
        var opts = new RemoteRuntimeOptions
        {
            IdentityMode = RemoteIdentityMode.TokenExchange,
            ClientId = "vais-a",
            ClientSecretRef = "secret://env/SECRET",
        };
        var act = () => opts.Validate();
        act.Should().Throw<InvalidOperationException>().WithMessage("*TokenExchangeEndpoint*");
    }

    [Fact]
    public void Validate_TokenExchange_MissingClientId_Throws()
    {
        var opts = new RemoteRuntimeOptions
        {
            IdentityMode = RemoteIdentityMode.TokenExchange,
            TokenExchangeEndpoint = new Uri("https://sts.example.com/token"),
            ClientSecretRef = "secret://env/SECRET",
        };
        var act = () => opts.Validate();
        act.Should().Throw<InvalidOperationException>().WithMessage("*ClientId*");
    }

    [Fact]
    public void Validate_TokenExchange_MissingClientSecretRef_Throws()
    {
        var opts = new RemoteRuntimeOptions
        {
            IdentityMode = RemoteIdentityMode.TokenExchange,
            TokenExchangeEndpoint = new Uri("https://sts.example.com/token"),
            ClientId = "vais-a",
        };
        var act = () => opts.Validate();
        act.Should().Throw<InvalidOperationException>().WithMessage("*ClientSecretRef*");
    }

    [Fact]
    public void Validate_TokenExchange_AllFieldsPresent_Succeeds()
    {
        var opts = new RemoteRuntimeOptions
        {
            IdentityMode = RemoteIdentityMode.TokenExchange,
            TokenExchangeEndpoint = new Uri("https://sts.example.com/token"),
            ClientId = "vais-a",
            ClientSecretRef = "secret://env/SECRET",
        };
        var act = () => opts.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_ServiceAccount_DefaultPath_Succeeds()
    {
        var opts = new RemoteRuntimeOptions { IdentityMode = RemoteIdentityMode.ServiceAccount };
        var act = () => opts.Validate();
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_ServiceAccount_EmptyPath_Throws()
    {
        var opts = new RemoteRuntimeOptions
        {
            IdentityMode = RemoteIdentityMode.ServiceAccount,
            ServiceAccountTokenPath = "",
        };
        var act = () => opts.Validate();
        act.Should().Throw<InvalidOperationException>().WithMessage("*ServiceAccountTokenPath*");
    }

    [Fact]
    public void Defaults_ForwardMode_AndFiveMinuteCache()
    {
        var opts = new RemoteRuntimeOptions();
        opts.IdentityMode.Should().Be(RemoteIdentityMode.Forward);
        opts.TokenCacheTtl.Should().Be(TimeSpan.FromMinutes(5));
        opts.ServiceAccountTokenPath.Should().Be("/var/run/secrets/tokens/vais-runtime-token");
    }
}
