// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Instantiation;

/// <summary>
/// Thrown by <see cref="IAgentManifestTranslator"/> when a stored <c>AgentManifest</c>
/// cannot be translated into <c>StatefulAgentOptions</c>. Carries a stable
/// <c>urn:vais-agents:*</c> <see cref="Urn"/> so the HTTP surface can map the failure
/// to a Problem Details response; the <see cref="Exception.Message"/> stays
/// partner-readable.
/// </summary>
/// <remarks>
/// The full URN catalogue lives in <see cref="ManifestInstantiationUrns"/>.
/// </remarks>
public sealed class ManifestInstantiationException : Exception
{
    /// <summary>URN identifying the failure class (e.g. <c>urn:vais-agents:model-provider-unsupported</c>).</summary>
    public string Urn { get; }

    /// <summary>Construct with the given URN and message.</summary>
    public ManifestInstantiationException(string urn, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(urn);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Urn = urn;
    }

    /// <summary>Construct with the given URN, message, and wrapped inner exception.</summary>
    public ManifestInstantiationException(string urn, string message, Exception innerException)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(urn);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Urn = urn;
    }
}

/// <summary>
/// Canonical URNs emitted by <see cref="ManifestInstantiationException"/>. The HTTP
/// control plane surfaces these unchanged in Problem Details responses.
/// </summary>
public static class ManifestInstantiationUrns
{
    /// <summary>Common prefix shared by every URN emitted from <see cref="ManifestInstantiationException"/>.</summary>
    public const string UrnPrefix = "urn:vais-agents:";

    /// <summary>No manifest registered for the requested agent id.</summary>
    public const string AgentNotFound = UrnPrefix + "agent-not-found";

    /// <summary>Registry lookup succeeded but the stored manifest lacks both <c>Model</c> and a loadable plugin handler.</summary>
    public const string HandlerNotLoaded = UrnPrefix + "handler-not-loaded";

    /// <summary>Manifest's <c>ModelSpec.Provider</c> does not match any registered <see cref="IModelProviderFactory"/>.</summary>
    public const string ModelProviderUnsupported = UrnPrefix + "model-provider-unsupported";

    /// <summary><c>Tools[].Source</c> uses an unknown prefix. Valid prefixes are <c>static:</c> / <c>mcp:</c> / <c>a2a:</c>.</summary>
    public const string ToolSourceUnknown = UrnPrefix + "tool-source-unknown";

    /// <summary><c>static:&lt;name&gt;</c> does not resolve in the registered <see cref="IStaticToolRegistry"/>.</summary>
    public const string ToolNotRegistered = UrnPrefix + "tool-not-registered";

    /// <summary><c>mcp:&lt;name&gt;</c> references a server name not declared in <c>AgentManifest.McpServers</c>.</summary>
    public const string McpServerNotDeclared = UrnPrefix + "mcp-server-not-declared";

    /// <summary><c>a2a:&lt;name&gt;</c> references an agent name not declared in <c>AgentManifest.A2ARemoteAgents</c>.</summary>
    public const string A2AAgentNotDeclared = UrnPrefix + "a2a-agent-not-declared";

    /// <summary>Guardrail ref name has no registered <see cref="IGuardrailFactory"/> for the requested layer.</summary>
    public const string GuardrailNotRegistered = UrnPrefix + "guardrail-not-registered";

    /// <summary>Guardrail factory rejected the supplied <c>params</c> (missing key, wrong type, bad value).</summary>
    public const string GuardrailParamsInvalid = UrnPrefix + "guardrail-params-invalid";

    /// <summary><see cref="SystemPromptSpec.TemplateRef"/> does not resolve in the registered <see cref="IPromptTemplateRegistry"/>.</summary>
    public const string PromptTemplateNotRegistered = UrnPrefix + "prompt-template-not-registered";

    /// <summary><see cref="SystemPromptSpec.FileRef"/> could not be read (missing, permissions, outside root).</summary>
    public const string PromptFileUnreadable = UrnPrefix + "prompt-file-unreadable";

    /// <summary><see cref="SystemPromptSpec"/> specifies more than one shape (inline + templateRef + fileRef are mutually exclusive).</summary>
    public const string PromptSpecAmbiguous = UrnPrefix + "prompt-spec-ambiguous";

    /// <summary>Plugin factory's <c>CreateAsync</c> threw during grain activation. v0.18 Pillar C.</summary>
    public const string PluginFactoryThrow = UrnPrefix + "plugin-factory-throw";

    /// <summary>Apply-time WARN — manifest has both a loaded-plugin <c>handler.TypeName</c> AND declarative <c>Model</c> fields. Plugin wins; declarative fields ignored. v0.18 Pillar C.</summary>
    public const string HandlerAndDeclarativeFieldsBothSet = UrnPrefix + "handler-and-declarative-fields-both-set";
}
