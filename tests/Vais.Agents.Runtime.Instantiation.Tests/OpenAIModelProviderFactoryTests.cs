// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using NSubstitute;
using Vais.Agents.Control;
using Vais.Agents.Runtime.Instantiation.ModelProviders;
using Xunit;

namespace Vais.Agents.Runtime.Instantiation.Tests;

public sealed class OpenAIModelProviderFactoryTests
{
    private static readonly ModelSpec BaseSpec = new("openai", "gpt-4o-mini", ApiKeyRef: "secret://openai-key");

    private static ISecretResolver SecretsReturning(string apiKey, string? endpoint = null)
    {
        var secrets = Substitute.For<ISecretResolver>();
        secrets.ResolveAsync("secret://openai-key", Arg.Any<CancellationToken>())
            .Returns(new ValueTask<string>(apiKey));
        if (endpoint is not null)
        {
            secrets.ResolveAsync("secret://custom-endpoint", Arg.Any<CancellationToken>())
                .Returns(new ValueTask<string>(endpoint));
        }
        return secrets;
    }

    [Fact]
    public async Task CreateAsync_WithoutBaseUrlRef_Succeeds()
    {
        var factory = new OpenAIModelProviderFactory();
        var provider = await factory.CreateAsync(BaseSpec, SecretsReturning("sk-test"), CancellationToken.None);
        provider.Should().NotBeNull();
    }

    [Fact]
    public async Task CreateAsync_WithValidBaseUrlRef_Succeeds()
    {
        var spec = BaseSpec with { BaseUrlRef = "secret://custom-endpoint" };
        var factory = new OpenAIModelProviderFactory();

        var provider = await factory.CreateAsync(
            spec,
            SecretsReturning("sk-test", "http://localhost:8010/v1"),
            CancellationToken.None);

        provider.Should().NotBeNull();
    }

    [Fact]
    public async Task CreateAsync_WithInvalidUri_ThrowsManifestInstantiationException()
    {
        var spec = BaseSpec with { BaseUrlRef = "secret://custom-endpoint" };
        var factory = new OpenAIModelProviderFactory();

        var act = () => factory.CreateAsync(
            spec,
            SecretsReturning("sk-test", "not a valid uri :::"),
            CancellationToken.None).AsTask();

        await act.Should().ThrowAsync<ManifestInstantiationException>()
            .WithMessage("*not a valid URI*");
    }

    [Fact]
    public async Task CreateAsync_WithBaseUrlRefResolvingToEmpty_ThrowsManifestInstantiationException()
    {
        var spec = BaseSpec with { BaseUrlRef = "secret://custom-endpoint" };
        var secrets = Substitute.For<ISecretResolver>();
        secrets.ResolveAsync("secret://openai-key", Arg.Any<CancellationToken>())
            .Returns(new ValueTask<string>("sk-test"));
        secrets.ResolveAsync("secret://custom-endpoint", Arg.Any<CancellationToken>())
            .Returns(new ValueTask<string>(string.Empty));

        var factory = new OpenAIModelProviderFactory();

        var act = () => factory.CreateAsync(spec, secrets, CancellationToken.None).AsTask();

        await act.Should().ThrowAsync<ManifestInstantiationException>()
            .WithMessage("*resolved to an empty value*");
    }

    [Fact]
    public async Task CreateAsync_MissingApiKeyRef_ThrowsManifestInstantiationException()
    {
        var spec = new ModelSpec("openai", "gpt-4o-mini");
        var factory = new OpenAIModelProviderFactory();

        var act = () => factory.CreateAsync(spec, Substitute.For<ISecretResolver>(), CancellationToken.None).AsTask();

        await act.Should().ThrowAsync<ManifestInstantiationException>()
            .WithMessage("*ApiKeyRef*");
    }

    [Fact]
    public void Provider_ReturnsOpenai()
    {
        new OpenAIModelProviderFactory().Provider.Should().Be("openai");
    }
}
