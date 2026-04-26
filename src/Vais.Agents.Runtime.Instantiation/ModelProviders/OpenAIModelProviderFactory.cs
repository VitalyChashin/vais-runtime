// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;
using Vais.Agents.Ai.MicrosoftAgentFramework;
using Vais.Agents.Control;

namespace Vais.Agents.Runtime.Instantiation.ModelProviders;

/// <summary>
/// <see cref="IModelProviderFactory"/> for OpenAI — <c>ModelSpec.Provider == "openai"</c>.
/// Builds an <see cref="IChatClient"/> via the OpenAI SDK's MEAI bridge and wraps
/// it as a <see cref="MafCompletionProvider"/>. Consumers who prefer Semantic
/// Kernel should register their own <see cref="IModelProviderFactory"/> and
/// inject an <c>SkCompletionProvider</c> instead.
/// <para>
/// When <see cref="ModelSpec.BaseUrlRef"/> is set, the resolved value is used as the
/// API endpoint — enabling any OpenAI-compatible service (local models, proxies, or
/// third-party providers such as SGR Agent) to be consumed without additional code.
/// </para>
/// </summary>
public sealed class OpenAIModelProviderFactory : IModelProviderFactory
{
    /// <summary>Provider name (case-insensitive match against <c>ModelSpec.Provider</c>).</summary>
    public const string ProviderName = "openai";

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
                $"OpenAI ModelSpec requires 'ApiKeyRef' (a secret:// URI resolving to an API key). Model id: '{spec.Id}'.");
        }

        string apiKey = await ResolveOrThrowAsync(secrets, spec.ApiKeyRef, "ApiKeyRef", cancellationToken);

        OpenAIClient openAiClient;
        if (!string.IsNullOrWhiteSpace(spec.BaseUrlRef))
        {
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
                    $"OpenAI BaseUrlRef '{spec.BaseUrlRef}' resolved to '{endpointStr}', which is not a valid URI.",
                    ex);
            }

            openAiClient = new OpenAIClient(new ApiKeyCredential(apiKey), new OpenAIClientOptions { Endpoint = endpoint });
        }
        else
        {
            openAiClient = new OpenAIClient(apiKey);
        }

        IChatClient chatClient = openAiClient.GetChatClient(spec.Id).AsIChatClient();
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
                $"OpenAI {fieldName} '{secretRef}' could not be resolved: {ex.Message}",
                ex);
        }

        if (string.IsNullOrWhiteSpace(resolved))
        {
            throw new ManifestInstantiationException(
                ManifestInstantiationUrns.ModelProviderUnsupported,
                $"OpenAI {fieldName} '{secretRef}' resolved to an empty value.");
        }

        return resolved;
    }
}
