// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Instantiation;

/// <summary>
/// Name-keyed registry of prompt templates. <see cref="SystemPromptSpec.TemplateRef"/>
/// resolves through this registry; <see cref="SystemPromptSpec.Variables"/> are
/// substituted at translate time via simple <c>{{key}}</c> replacement — no
/// expression evaluation, no escaping gymnastics.
/// </summary>
/// <remarks>
/// Absent registry ⇒ any <c>TemplateRef</c> in a manifest fails translation
/// with <see cref="ManifestInstantiationUrns.PromptTemplateNotRegistered"/>.
/// Registrations happen via
/// <c>services.AddPromptTemplateRegistry(b =&gt; b.Add("triage-intro", "You are..."))</c>
/// at host-startup time.
/// </remarks>
public interface IPromptTemplateRegistry
{
    /// <summary>Resolve a named template. Returns <c>null</c> when the name is not registered.</summary>
    string? Get(string name);
}

/// <summary>Builder surface for <see cref="IPromptTemplateRegistry"/>.</summary>
public interface IPromptTemplateRegistryBuilder
{
    /// <summary>Register a template under <paramref name="name"/>. Duplicate names throw.</summary>
    IPromptTemplateRegistryBuilder Add(string name, string template);
}
