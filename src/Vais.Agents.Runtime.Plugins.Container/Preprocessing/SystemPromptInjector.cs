// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Vais.Agents.Runtime.Instantiation;

namespace Vais.Agents.Runtime.Plugins.Container.Preprocessing;

internal sealed class SystemPromptInjector : IAgentPreprocessor
{
    private readonly IPromptTemplateRegistry? _templates;
    private readonly IPromptFileLoader? _fileLoader;

    internal SystemPromptInjector(
        IPromptTemplateRegistry? templates,
        IPromptFileLoader? fileLoader)
    {
        _templates = templates;
        _fileLoader = fileLoader;
    }

    public int Order => 10;

    public async ValueTask<IReadOnlyList<ChatTurn>> ProcessAsync(
        AgentPreprocessorContext context,
        IReadOnlyList<ChatTurn> messages,
        CancellationToken cancellationToken = default)
    {
        var text = !string.IsNullOrEmpty(context.GrainState.SystemPrompt)
            ? context.GrainState.SystemPrompt
            : await ResolveManifestPromptAsync(context.Manifest.SystemPrompt, cancellationToken)
                .ConfigureAwait(false);

        if (string.IsNullOrEmpty(text))
            return messages;

        var result = new ChatTurn[messages.Count + 1];
        result[0] = new ChatTurn(AgentChatRole.System, text);
        for (var i = 0; i < messages.Count; i++) result[i + 1] = messages[i];
        return result;
    }

    private async ValueTask<string?> ResolveManifestPromptAsync(
        SystemPromptSpec? spec,
        CancellationToken ct)
    {
        if (spec is null) return null;

        string? raw = null;

        if (spec.Inline is not null)
        {
            raw = spec.Inline;
        }
        else if (spec.TemplateRef is not null)
        {
            if (_templates is null)
                throw new InvalidOperationException(
                    $"[{ContainerPluginUrns.SystemPromptResolutionFailed}] " +
                    $"SystemPromptSpec.TemplateRef '{spec.TemplateRef}' requested but no IPromptTemplateRegistry is registered.");
            raw = _templates.Get(spec.TemplateRef)
                  ?? throw new InvalidOperationException(
                      $"[{ContainerPluginUrns.SystemPromptResolutionFailed}] " +
                      $"SystemPromptSpec.TemplateRef '{spec.TemplateRef}' not found in IPromptTemplateRegistry.");
        }
        else if (spec.FileRef is not null)
        {
            if (_fileLoader is null)
                throw new InvalidOperationException(
                    $"[{ContainerPluginUrns.SystemPromptResolutionFailed}] " +
                    $"SystemPromptSpec.FileRef '{spec.FileRef}' requested but no IPromptFileLoader is registered.");
            raw = await _fileLoader.LoadAsync(spec.FileRef, ct).ConfigureAwait(false);
        }

        if (raw is null) return null;

        if (spec.Variables is { Count: > 0 })
            foreach (var (key, value) in spec.Variables)
                raw = raw.Replace("{{" + key + "}}", value, StringComparison.Ordinal);

        return raw;
    }
}
