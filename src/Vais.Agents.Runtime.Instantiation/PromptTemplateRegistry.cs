// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Collections.Concurrent;

namespace Vais.Agents.Runtime.Instantiation;

internal sealed class PromptTemplateRegistry : IPromptTemplateRegistry
{
    private readonly IReadOnlyDictionary<string, string> _templates;

    public PromptTemplateRegistry(IReadOnlyDictionary<string, string> templates)
    {
        ArgumentNullException.ThrowIfNull(templates);
        _templates = templates;
    }

    public string? Get(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _templates.TryGetValue(name, out var template) ? template : null;
    }
}

internal sealed class PromptTemplateRegistryBuilder : IPromptTemplateRegistryBuilder
{
    private readonly ConcurrentDictionary<string, string> _templates = new(StringComparer.Ordinal);

    public IPromptTemplateRegistryBuilder Add(string name, string template)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(template);

        if (!_templates.TryAdd(name, template))
        {
            throw new InvalidOperationException(
                $"A prompt template named '{name}' is already registered. Template names must be unique.");
        }

        return this;
    }

    public IPromptTemplateRegistry Build() => new PromptTemplateRegistry(_templates);
}
