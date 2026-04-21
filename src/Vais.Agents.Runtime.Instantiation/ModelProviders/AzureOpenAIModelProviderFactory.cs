// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.ClientModel;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Vais.Agents.Ai.MicrosoftAgentFramework;
using Vais.Agents.Control;

namespace Vais.Agents.Runtime.Instantiation.ModelProviders;

/// <summary>
/// <see cref="IModelProviderFactory"/> for Azure OpenAI — `ModelSpec.Provider
/// == "azure-openai"`. Requires both <see cref="ModelSpec.BaseUrlRef"/>
/// (resolving to the Azure endpoint URL) and <see cref="ModelSpec.ApiKeyRef"/>
/// (resolving to the Azure API key); <see cref="ModelSpec.Id"/> is the Azure
/// deployment name (not the OpenAI model id).
/// </summary>
public sealed class AzureOpenAIModelProviderFactory : IModelProviderFactory
{
    /// <summary>Provider name (case-insensitive match against <c>ModelSpec.Provider</c>).</summary>
    public const string ProviderName = "azure-openai";

    /// <inheritdoc />
    public string Provider => ProviderName;

    /// <inheritdoc />
    public async ValueTask<ICompletionProvider> CreateAsync(
        ModelSpec spec,
        ISecretResolver secrets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(secrets);

        if (string.IsNullOrWhiteSpace(spec.ApiKeyRef))
        {
            throw new ManifestInstantiationException(
                ManifestInstantiationUrns.ModelProviderUnsupported,
                $"AzureOpenAI ModelSpec requires 'ApiKeyRef'. Deployment: '{spec.Id}'.");
        }

        if (string.IsNullOrWhiteSpace(spec.BaseUrlRef))
        {
            throw new ManifestInstantiationException(
                ManifestInstantiationUrns.ModelProviderUnsupported,
                $"AzureOpenAI ModelSpec requires 'BaseUrlRef' (a secret:// URI resolving to the Azure endpoint URL). Deployment: '{spec.Id}'.");
        }

        string apiKey = await ResolveOrThrowAsync(secrets, spec.ApiKeyRef, "ApiKeyRef", cancellationToken);
        string endpointStr = await ResolveOrThrowAsync(secrets, spec.BaseUrlRef, "BaseUrlRef", cancellationToken);

        Uri endpoint;
        try
        {
            endpoint = new Uri(endpointStr);
        }
        catch (UriFormatException ex)
        {
            throw new ManifestInstantiationException(
                ManifestInstantiationUrns.ModelProviderUnsupported,
                $"AzureOpenAI BaseUrlRef '{spec.BaseUrlRef}' resolved to '{endpointStr}', which is not a valid URI.",
                ex);
        }

        var azureClient = new AzureOpenAIClient(endpoint, new ApiKeyCredential(apiKey));
        IChatClient chatClient = azureClient.GetChatClient(spec.Id).AsIChatClient();
        return new MafCompletionProvider(chatClient, modelId: spec.Id);
    }

    private static async Task<string> ResolveOrThrowAsync(
        ISecretResolver secrets,
        string secretRef,
        string fieldName,
        CancellationToken cancellationToken)
    {
        string resolved;
        try
        {
            resolved = await secrets.ResolveAsync(secretRef, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new ManifestInstantiationException(
                ManifestInstantiationUrns.ModelProviderUnsupported,
                $"AzureOpenAI {fieldName} '{secretRef}' could not be resolved: {ex.Message}",
                ex);
        }

        if (string.IsNullOrWhiteSpace(resolved))
        {
            throw new ManifestInstantiationException(
                ManifestInstantiationUrns.ModelProviderUnsupported,
                $"AzureOpenAI {fieldName} '{secretRef}' resolved to an empty value.");
        }

        return resolved;
    }
}
