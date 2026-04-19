// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Vais.Agents.Control.Manifests;

/// <summary>
/// Parses <see cref="AgentManifest"/> records from JSON text. Accepts either a
/// single manifest document or a JSON array of manifests. Schema matches the
/// v0.6 envelope: <c>apiVersion</c> + <c>kind</c> + <c>metadata</c> + <c>spec</c>.
/// Validates per the rules in the manifest-schema companion doc; throws
/// <see cref="AgentManifestValidationException"/> with every violation found in
/// one pass.
/// </summary>
/// <remarks>
/// <para>
/// <b>Wire shape.</b> The HTTP control plane consumes and emits this format
/// natively; file-on-disk / CLI use the YAML loader, which normalises YAML to
/// JSON and delegates to this class. Two formats, one validation core.
/// </para>
/// <para>
/// <b>Field-order preservation.</b> <see cref="JsonDocument"/> preserves object
/// property order, which matters for <see cref="AgentManifest.Reasoning"/>
/// schemas under SGR (cascade / routing / cycle patterns rely on the LLM
/// filling fields top-to-bottom in declaration order). The loader stores
/// <see cref="ReasoningSpec.Schema"/> as the raw <see cref="JsonElement"/> so
/// ordering survives all the way into the runtime.
/// </para>
/// </remarks>
public sealed class JsonAgentManifestLoader : IAgentManifestLoader
{
    private const string ExpectedApiVersion = "vais.agents/v1";
    private const string ExpectedKind = "Agent";

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<AgentManifest>> LoadFromStringAsync(string content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var errors = new List<string>();
        var result = ParseAndValidate(content, errors);
        if (errors.Count > 0)
        {
            throw new AgentManifestValidationException(errors);
        }
        return ValueTask.FromResult<IReadOnlyList<AgentManifest>>(result);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<AgentManifest>> LoadFromFileAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return await LoadFromStringAsync(content, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<AgentManifest>> LoadFromDirectoryAsync(string directory, string searchPattern, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(searchPattern);
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(directory);
        }

        var files = Directory.GetFiles(directory, searchPattern, SearchOption.TopDirectoryOnly);
        Array.Sort(files, StringComparer.Ordinal);
        var all = new List<AgentManifest>();
        var errors = new List<string>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            try
            {
                var items = ParseAndValidate(content, errors, file);
                all.AddRange(items);
            }
            catch (JsonException ex)
            {
                errors.Add($"{Path.GetFileName(file)}: parse error — {ex.Message}");
            }
        }

        CheckDuplicateIds(all, errors);

        if (errors.Count > 0)
        {
            throw new AgentManifestValidationException(errors);
        }
        return all;
    }

    internal static IReadOnlyList<AgentManifest> ParseAndValidate(string content, List<string> errors, string? sourceHint = null)
    {
        var prefix = sourceHint is null ? string.Empty : $"{Path.GetFileName(sourceHint)}: ";
        if (string.IsNullOrWhiteSpace(content))
        {
            errors.Add($"{prefix}empty document");
            return Array.Empty<AgentManifest>();
        }

        // Preserve property order on the root so reasoning schemas stay SGR-usable.
        using var doc = JsonDocument.Parse(content, new JsonDocumentOptions { AllowTrailingCommas = true });

        var manifests = new List<AgentManifest>();
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var parsed = ParseSingle(item, errors, $"{prefix}[{index}] ");
                if (parsed is not null)
                {
                    manifests.Add(parsed);
                }
                index++;
            }
        }
        else
        {
            var parsed = ParseSingle(doc.RootElement, errors, prefix);
            if (parsed is not null)
            {
                manifests.Add(parsed);
            }
        }

        if (sourceHint is null)
        {
            CheckDuplicateIds(manifests, errors);
        }
        return manifests;
    }

    private static AgentManifest? ParseSingle(JsonElement root, List<string> errors, string prefix)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{prefix}document must be a JSON object");
            return null;
        }

        var apiVersion = root.TryGetProperty("apiVersion", out var av) ? av.GetString() : null;
        if (!string.Equals(apiVersion, ExpectedApiVersion, StringComparison.Ordinal))
        {
            errors.Add($"{prefix}unexpected apiVersion '{apiVersion ?? "<null>"}' (expected '{ExpectedApiVersion}')");
        }

        var kind = root.TryGetProperty("kind", out var k) ? k.GetString() : null;
        if (!string.Equals(kind, ExpectedKind, StringComparison.Ordinal))
        {
            errors.Add($"{prefix}unexpected kind '{kind ?? "<null>"}' (expected '{ExpectedKind}')");
        }

        if (!root.TryGetProperty("metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{prefix}missing or invalid metadata block");
            return null;
        }

        var (id, version, description, labels, annotations) = ParseMetadata(metadata, errors, prefix);
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(version))
        {
            return null;
        }

        var spec = root.TryGetProperty("spec", out var s) && s.ValueKind == JsonValueKind.Object ? s : default;
        return BuildManifest(id, version, description, labels, annotations, spec, errors, prefix);
    }

    private static (string Id, string Version, string? Description, IReadOnlyDictionary<string, string>? Labels, IReadOnlyDictionary<string, string>? Annotations)
        ParseMetadata(JsonElement metadata, List<string> errors, string prefix)
    {
        var id = metadata.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        var version = metadata.TryGetProperty("version", out var vEl) ? vEl.GetString() : null;
        var description = metadata.TryGetProperty("description", out var dEl) ? dEl.GetString() : null;

        if (string.IsNullOrEmpty(id))
        {
            errors.Add($"{prefix}metadata.id is required");
            id = string.Empty;
        }
        else if (!ManifestValidation.IsValidId(id))
        {
            errors.Add($"{prefix}metadata.id '{id}' does not match ^[a-z][a-z0-9-]{{0,62}}$");
        }

        if (string.IsNullOrEmpty(version))
        {
            errors.Add($"{prefix}metadata.version is required");
            version = string.Empty;
        }
        else if (!ManifestValidation.IsValidVersion(version))
        {
            errors.Add($"{prefix}metadata.version '{version}' does not match ^\\d+\\.\\d+(\\.\\d+)?$");
        }

        var labels = ParseStringMap(metadata, "labels", errors, prefix, validateLabelKeys: true);
        var annotations = ParseStringMap(metadata, "annotations", errors, prefix, validateLabelKeys: false);
        return (id, version, description, labels, annotations);
    }

    private static IReadOnlyDictionary<string, string>? ParseStringMap(JsonElement parent, string name, List<string> errors, string prefix, bool validateLabelKeys)
    {
        if (!parent.TryGetProperty(name, out var mapEl) || mapEl.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var map = new Dictionary<string, string>();
        foreach (var prop in mapEl.EnumerateObject())
        {
            if (validateLabelKeys && !ManifestValidation.IsValidLabelKey(prop.Name))
            {
                errors.Add($"{prefix}{name} key '{prop.Name}' does not match K8s label key format");
            }
            var value = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : prop.Value.ToString();
            if (value is not null && value.Length > 63 && validateLabelKeys)
            {
                errors.Add($"{prefix}{name} value for '{prop.Name}' exceeds 63 chars");
            }
            map[prop.Name] = value ?? string.Empty;
        }
        return map;
    }

    private static AgentManifest BuildManifest(
        string id, string version, string? description,
        IReadOnlyDictionary<string, string>? labels,
        IReadOnlyDictionary<string, string>? annotations,
        JsonElement spec, List<string> errors, string prefix)
    {
        // Legacy v0.4 required fields (handler, protocols, tools) with sensible defaults
        // when the manifest drives behaviour declaratively via spec.model / spec.systemPrompt.
        var handler = spec.ValueKind == JsonValueKind.Object && spec.TryGetProperty("handler", out var handlerEl)
            ? ParseHandler(handlerEl, errors, prefix)
            : new AgentHandlerRef("declarative");

        var protocols = ParseProtocols(spec, errors, prefix);
        var tools = ParseTools(spec, errors, prefix);

        // v0.6 additive layer.
        var model = spec.ValueKind == JsonValueKind.Object && spec.TryGetProperty("model", out var modelEl)
            ? ParseModel(modelEl, errors, prefix) : null;
        var systemPrompt = spec.ValueKind == JsonValueKind.Object && spec.TryGetProperty("systemPrompt", out var spEl)
            ? ParseSystemPrompt(spEl, errors, prefix) : null;
        var mcpServers = spec.ValueKind == JsonValueKind.Object && spec.TryGetProperty("mcpServers", out var mcpEl)
            ? ParseMcpServers(mcpEl, errors, prefix) : null;
        var guardrails = spec.ValueKind == JsonValueKind.Object && spec.TryGetProperty("guardrails", out var grEl)
            ? ParseGuardrails(grEl, errors, prefix) : null;
        var handoffs = spec.ValueKind == JsonValueKind.Object && spec.TryGetProperty("handoffs", out var hoEl)
            ? ParseHandoffs(hoEl, errors, prefix) : null;
        var budget = spec.ValueKind == JsonValueKind.Object && spec.TryGetProperty("budget", out var budEl)
            ? ParseBudget(budEl, errors, prefix) : null;
        var contextProviders = spec.ValueKind == JsonValueKind.Object && spec.TryGetProperty("contextProviders", out var cpEl)
            ? ParseContextProviders(cpEl, errors, prefix) : null;
        JsonElement? outputSchema = spec.ValueKind == JsonValueKind.Object && spec.TryGetProperty("outputSchema", out var osEl)
            ? osEl.Clone() : null;

        var agentMode = AgentMode.ToolCalling;
        if (spec.ValueKind == JsonValueKind.Object && spec.TryGetProperty("agentMode", out var amEl) && amEl.ValueKind == JsonValueKind.String)
        {
            var raw = amEl.GetString();
            if (!Enum.TryParse(raw, ignoreCase: true, out agentMode))
            {
                errors.Add($"{prefix}spec.agentMode '{raw}' is not a known AgentMode value");
            }
        }

        var reasoning = spec.ValueKind == JsonValueKind.Object && spec.TryGetProperty("reasoning", out var rEl)
            ? ParseReasoning(rEl, errors, prefix) : null;
        var observability = spec.ValueKind == JsonValueKind.Object && spec.TryGetProperty("observability", out var obEl)
            ? ParseObservability(obEl, errors, prefix) : null;

        var memory = spec.ValueKind == JsonValueKind.Object && spec.TryGetProperty("memory", out var memEl)
            ? ParseMemory(memEl, errors, prefix) : null;
        var identity = spec.ValueKind == JsonValueKind.Object && spec.TryGetProperty("identity", out var idEl2)
            ? ParseIdentity(idEl2, errors, prefix) : null;
        var autoscaling = spec.ValueKind == JsonValueKind.Object && spec.TryGetProperty("autoscaling", out var asEl)
            ? ParseAutoscaling(asEl, errors, prefix) : null;

        return new AgentManifest(
            id, version, handler, protocols, tools,
            Memory: memory,
            Identity: identity,
            Autoscaling: autoscaling,
            Description: description,
            Labels: labels)
        {
            Model = model,
            SystemPrompt = systemPrompt,
            McpServers = mcpServers,
            Guardrails = guardrails,
            Handoffs = handoffs,
            Budget = budget,
            ContextProviders = contextProviders,
            OutputSchema = outputSchema,
            AgentMode = agentMode,
            Reasoning = reasoning,
            Observability = observability,
            Annotations = annotations,
        };
    }

    private static AgentHandlerRef ParseHandler(JsonElement el, List<string> errors, string prefix)
    {
        if (el.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{prefix}spec.handler must be an object");
            return new AgentHandlerRef("declarative");
        }
        var name = el.TryGetProperty("name", out var nEl) ? nEl.GetString()
                 : el.TryGetProperty("typeName", out var tnEl) ? tnEl.GetString() : null;
        var assembly = el.TryGetProperty("assembly", out var aEl) ? aEl.GetString()
                     : el.TryGetProperty("assemblyName", out var anEl) ? anEl.GetString() : null;
        if (string.IsNullOrEmpty(name))
        {
            errors.Add($"{prefix}spec.handler.name is required when spec.handler is set");
            name = "declarative";
        }
        return new AgentHandlerRef(name, assembly);
    }

    private static IReadOnlyList<ProtocolBinding> ParseProtocols(JsonElement spec, List<string> errors, string prefix)
    {
        if (spec.ValueKind != JsonValueKind.Object || !spec.TryGetProperty("protocols", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ProtocolBinding>();
        }
        var list = new List<ProtocolBinding>();
        foreach (var item in arr.EnumerateArray())
        {
            var kind = item.TryGetProperty("kind", out var kEl) ? kEl.GetString() : null;
            var endpoint = item.TryGetProperty("path", out var pEl) ? pEl.GetString()
                         : item.TryGetProperty("endpoint", out var eEl) ? eEl.GetString() : null;
            if (string.IsNullOrEmpty(kind))
            {
                errors.Add($"{prefix}spec.protocols[].kind is required");
                continue;
            }
            list.Add(new ProtocolBinding(kind, endpoint));
        }
        return list;
    }

    private static IReadOnlyList<ToolRef> ParseTools(JsonElement spec, List<string> errors, string prefix)
    {
        if (spec.ValueKind != JsonValueKind.Object || !spec.TryGetProperty("tools", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<ToolRef>();
        }
        var list = new List<ToolRef>();
        foreach (var item in arr.EnumerateArray())
        {
            var name = item.TryGetProperty("name", out var nEl) ? nEl.GetString() : null;
            var source = item.TryGetProperty("source", out var sEl) ? sEl.GetString() : null;
            if (string.IsNullOrEmpty(name))
            {
                errors.Add($"{prefix}spec.tools[].name is required");
                continue;
            }
            list.Add(new ToolRef(name, source));
        }
        return list;
    }

    private static ModelSpec ParseModel(JsonElement el, List<string> errors, string prefix)
    {
        var provider = el.TryGetProperty("provider", out var pEl) ? pEl.GetString() : null;
        var modelId = el.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (string.IsNullOrEmpty(provider)) errors.Add($"{prefix}spec.model.provider is required");
        if (string.IsNullOrEmpty(modelId)) errors.Add($"{prefix}spec.model.id is required");

        return new ModelSpec(
            provider ?? string.Empty,
            modelId ?? string.Empty,
            ApiKeyRef: el.TryGetProperty("apiKeyRef", out var kEl) ? kEl.GetString() : null,
            BaseUrlRef: el.TryGetProperty("baseUrlRef", out var bEl) ? bEl.GetString() : null,
            Temperature: el.TryGetProperty("temperature", out var tEl) && tEl.ValueKind == JsonValueKind.Number ? tEl.GetDouble() : null,
            TopP: el.TryGetProperty("topP", out var tpEl) && tpEl.ValueKind == JsonValueKind.Number ? tpEl.GetDouble() : null,
            MaxTokens: el.TryGetProperty("maxTokens", out var mtEl) && mtEl.ValueKind == JsonValueKind.Number ? mtEl.GetInt32() : null,
            ResponseFormat: el.TryGetProperty("responseFormat", out var rfEl) ? rfEl.GetString() : null);
    }

    private static SystemPromptSpec ParseSystemPrompt(JsonElement el, List<string> errors, string prefix)
    {
        var inline = el.TryGetProperty("inline", out var iEl) ? iEl.GetString() : null;
        var templateRef = el.TryGetProperty("templateRef", out var tEl) ? tEl.GetString() : null;
        var fileRef = el.TryGetProperty("fileRef", out var fEl) ? fEl.GetString() : null;

        var set = (inline is not null ? 1 : 0) + (templateRef is not null ? 1 : 0) + (fileRef is not null ? 1 : 0);
        if (set != 1)
        {
            errors.Add($"{prefix}spec.systemPrompt must set exactly one of inline / templateRef / fileRef (got {set})");
        }

        Dictionary<string, string>? variables = null;
        if (el.TryGetProperty("variables", out var vEl) && vEl.ValueKind == JsonValueKind.Object)
        {
            variables = new Dictionary<string, string>();
            foreach (var prop in vEl.EnumerateObject())
            {
                variables[prop.Name] = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() ?? "" : prop.Value.ToString();
            }
        }
        return new SystemPromptSpec(inline, templateRef, fileRef, variables);
    }

    private static IReadOnlyList<McpServerRef> ParseMcpServers(JsonElement arr, List<string> errors, string prefix)
    {
        if (arr.ValueKind != JsonValueKind.Array) return Array.Empty<McpServerRef>();
        var list = new List<McpServerRef>();
        foreach (var item in arr.EnumerateArray())
        {
            var name = item.TryGetProperty("name", out var nEl) ? nEl.GetString() : null;
            var transport = item.TryGetProperty("transport", out var tEl) ? tEl.GetString() : null;
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(transport))
            {
                errors.Add($"{prefix}spec.mcpServers[].name + transport are required");
                continue;
            }
            var command = item.TryGetProperty("command", out var cEl) ? cEl.GetString() : null;
            var url = item.TryGetProperty("url", out var uEl) ? uEl.GetString() : null;
            if ((command is null) == (url is null))
            {
                errors.Add($"{prefix}spec.mcpServers[{name}] must set exactly one of command (for stdio) or url (for http/sse)");
            }
            var args = item.TryGetProperty("args", out var aEl) && aEl.ValueKind == JsonValueKind.Array
                ? aEl.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray()
                : null;
            Dictionary<string, string>? env = null;
            if (item.TryGetProperty("env", out var eEl) && eEl.ValueKind == JsonValueKind.Object)
            {
                env = new Dictionary<string, string>();
                foreach (var p in eEl.EnumerateObject()) env[p.Name] = p.Value.GetString() ?? "";
            }
            var authRef = item.TryGetProperty("authRef", out var arEl) ? arEl.GetString() : null;
            var toolsFilter = item.TryGetProperty("tools", out var tlEl) && tlEl.ValueKind == JsonValueKind.Array
                ? tlEl.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray()
                : null;
            list.Add(new McpServerRef(name, transport, command, args, url, env, authRef, toolsFilter));
        }
        return list;
    }

    private static GuardrailsSpec ParseGuardrails(JsonElement el, List<string> errors, string prefix)
    {
        return new GuardrailsSpec(
            Input: ParseGuardrailList(el, "input", errors, prefix),
            Output: ParseGuardrailList(el, "output", errors, prefix),
            Tool: ParseGuardrailList(el, "tool", errors, prefix));
    }

    private static IReadOnlyList<GuardrailRef>? ParseGuardrailList(JsonElement parent, string name, List<string> errors, string prefix)
    {
        if (!parent.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        var list = new List<GuardrailRef>();
        foreach (var item in arr.EnumerateArray())
        {
            var n = item.TryGetProperty("name", out var nEl) ? nEl.GetString() : null;
            if (string.IsNullOrEmpty(n))
            {
                errors.Add($"{prefix}spec.guardrails.{name}[].name is required");
                continue;
            }
            JsonElement? p = item.TryGetProperty("params", out var pEl) ? pEl.Clone() : null;
            list.Add(new GuardrailRef(n, p));
        }
        return list;
    }

    private static IReadOnlyList<HandoffRef> ParseHandoffs(JsonElement arr, List<string> errors, string prefix)
    {
        if (arr.ValueKind != JsonValueKind.Array) return Array.Empty<HandoffRef>();
        var list = new List<HandoffRef>();
        foreach (var item in arr.EnumerateArray())
        {
            var to = item.TryGetProperty("toAgent", out var tEl) ? tEl.GetString() : null;
            if (string.IsNullOrEmpty(to)) { errors.Add($"{prefix}spec.handoffs[].toAgent is required"); continue; }
            list.Add(new HandoffRef(
                to,
                When: item.TryGetProperty("when", out var wEl) ? wEl.GetString() : null,
                CarryHistory: item.TryGetProperty("carryHistory", out var chEl) && chEl.ValueKind != JsonValueKind.Null
                    ? chEl.GetBoolean() : null));
        }
        return list;
    }

    private static RunBudget ParseBudget(JsonElement el, List<string> errors, string prefix)
    {
        int? max(string key)
        {
            if (!el.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Number) return null;
            var n = v.GetInt32();
            if (n <= 0) { errors.Add($"{prefix}spec.budget.{key} must be > 0"); return null; }
            return n;
        }
        TimeSpan? duration = null;
        if (el.TryGetProperty("maxDuration", out var dEl) && dEl.ValueKind == JsonValueKind.String)
        {
            var raw = dEl.GetString();
            if (!ManifestValidation.TryParseDuration(raw!, out var parsed))
            {
                errors.Add($"{prefix}spec.budget.maxDuration '{raw}' is not a valid duration");
            }
            else
            {
                duration = parsed;
            }
        }
        return new RunBudget(
            MaxTurns: max("maxTurns"),
            MaxToolCalls: max("maxToolCalls"),
            MaxPromptTokens: max("maxPromptTokens"),
            MaxCompletionTokens: max("maxCompletionTokens"),
            MaxDuration: duration);
    }

    private static IReadOnlyList<ContextProviderRef> ParseContextProviders(JsonElement arr, List<string> errors, string prefix)
    {
        if (arr.ValueKind != JsonValueKind.Array) return Array.Empty<ContextProviderRef>();
        var list = new List<ContextProviderRef>();
        foreach (var item in arr.EnumerateArray())
        {
            var n = item.TryGetProperty("name", out var nEl) ? nEl.GetString() : null;
            if (string.IsNullOrEmpty(n)) { errors.Add($"{prefix}spec.contextProviders[].name is required"); continue; }
            JsonElement? p = item.TryGetProperty("params", out var pEl) ? pEl.Clone() : null;
            list.Add(new ContextProviderRef(n, p));
        }
        return list;
    }

    private static ReasoningSpec ParseReasoning(JsonElement el, List<string> errors, string prefix)
    {
        var patternStr = el.TryGetProperty("pattern", out var pEl) ? pEl.GetString() : null;
        if (!Enum.TryParse<ReasoningPattern>(patternStr, ignoreCase: true, out var pattern))
        {
            errors.Add($"{prefix}spec.reasoning.pattern '{patternStr}' must be cascade | routing | cycle");
        }
        JsonElement? schema = el.TryGetProperty("schema", out var sEl) ? sEl.Clone() : null;
        var schemaRef = el.TryGetProperty("schemaRef", out var srEl) ? srEl.GetString() : null;
        if ((schema is null) == (schemaRef is null))
        {
            errors.Add($"{prefix}spec.reasoning must set exactly one of schema / schemaRef");
        }
        return new ReasoningSpec(
            pattern,
            schema,
            schemaRef,
            MaxIterations: el.TryGetProperty("maxIterations", out var miEl) && miEl.ValueKind == JsonValueKind.Number ? miEl.GetInt32() : null,
            MaxClarifications: el.TryGetProperty("maxClarifications", out var mcEl) && mcEl.ValueKind == JsonValueKind.Number ? mcEl.GetInt32() : null);
    }

    private static ObservabilitySpec ParseObservability(JsonElement el, List<string> _, string __)
    {
        Dictionary<string, string>? tags = null;
        if (el.TryGetProperty("tags", out var tEl) && tEl.ValueKind == JsonValueKind.Object)
        {
            tags = new Dictionary<string, string>();
            foreach (var p in tEl.EnumerateObject()) tags[p.Name] = p.Value.GetString() ?? "";
        }
        return new ObservabilitySpec(
            LangfuseProject: el.TryGetProperty("langfuseProject", out var lEl) ? lEl.GetString() : null,
            SamplingRate: el.TryGetProperty("samplingRate", out var sEl) && sEl.ValueKind == JsonValueKind.Number ? sEl.GetDouble() : null,
            Tags: tags,
            TracingEnabled: el.TryGetProperty("tracingEnabled", out var teEl) && teEl.ValueKind != JsonValueKind.Null ? teEl.GetBoolean() : null);
    }

    private static MemoryRef ParseMemory(JsonElement el, List<string> errors, string prefix)
    {
        var kind = el.TryGetProperty("kind", out var kEl) ? kEl.GetString()
                 : el.TryGetProperty("provider", out var pEl2) ? pEl2.GetString() : null;
        if (string.IsNullOrEmpty(kind)) { errors.Add($"{prefix}spec.memory.kind is required"); kind = "unknown"; }
        return new MemoryRef(
            kind,
            ConnectionName: el.TryGetProperty("connectionRef", out var cEl) ? cEl.GetString()
                          : el.TryGetProperty("connectionName", out var cnEl) ? cnEl.GetString() : null)
        {
            Scope = el.TryGetProperty("scope", out var sEl) ? sEl.GetString() : null,
            HistoryReducer = el.TryGetProperty("historyReducer", out var hrEl) && hrEl.ValueKind == JsonValueKind.String
                ? hrEl.GetString()
                : (hrEl.ValueKind == JsonValueKind.Object && hrEl.TryGetProperty("name", out var hrnEl) ? hrnEl.GetString() : null),
        };
    }

    private static IdentityRef ParseIdentity(JsonElement el, List<string> errors, string prefix)
    {
        var ident = new IdentityRef(
            InboundAuth: el.TryGetProperty("inboundAuth", out var iaEl) ? iaEl.GetString() : null,
            OutboundCredentials: el.TryGetProperty("outboundCredentialsRef", out var ocEl) ? ocEl.GetString() : null);
        IReadOnlyList<OutboundCredentialRef>? creds = null;
        if (el.TryGetProperty("outboundCredentials", out var credsEl) && credsEl.ValueKind == JsonValueKind.Array)
        {
            var list = new List<OutboundCredentialRef>();
            foreach (var item in credsEl.EnumerateArray())
            {
                var n = item.TryGetProperty("name", out var nEl) ? nEl.GetString() : null;
                var r = item.TryGetProperty("ref", out var rEl) ? rEl.GetString() : null;
                var t = item.TryGetProperty("type", out var tEl) ? tEl.GetString() : null;
                if (string.IsNullOrEmpty(n) || string.IsNullOrEmpty(r) || string.IsNullOrEmpty(t))
                {
                    errors.Add($"{prefix}spec.identity.outboundCredentials[].{{name,ref,type}} all required");
                    continue;
                }
                list.Add(new OutboundCredentialRef(n, r, t));
            }
            creds = list;
        }
        Dictionary<string, string>? claims = null;
        if (el.TryGetProperty("inboundClaims", out var icEl) && icEl.ValueKind == JsonValueKind.Object)
        {
            claims = new Dictionary<string, string>();
            foreach (var p in icEl.EnumerateObject()) claims[p.Name] = p.Value.GetString() ?? "";
        }
        return ident with { Credentials = creds, InboundClaims = claims };
    }

    private static AutoscalingSpec ParseAutoscaling(JsonElement el, List<string> errors, string prefix)
    {
        var min = el.TryGetProperty("minReplicas", out var minEl) && minEl.ValueKind == JsonValueKind.Number ? minEl.GetInt32() : 0;
        var max = el.TryGetProperty("maxReplicas", out var maxEl) && maxEl.ValueKind == JsonValueKind.Number ? (int?)maxEl.GetInt32() : null;
        if (max is int m && min > m)
        {
            errors.Add($"{prefix}spec.autoscaling.minReplicas ({min}) exceeds maxReplicas ({m})");
        }
        string? targetMetric = null;
        double? targetValue = null;
        if (el.TryGetProperty("target", out var tEl))
        {
            if (tEl.ValueKind == JsonValueKind.String)
            {
                targetMetric = tEl.GetString();
            }
            else if (tEl.ValueKind == JsonValueKind.Object)
            {
                targetMetric = tEl.TryGetProperty("metric", out var tmEl) ? tmEl.GetString() : null;
                if (tEl.TryGetProperty("value", out var tvEl) && tvEl.ValueKind == JsonValueKind.Number)
                {
                    targetValue = tvEl.GetDouble();
                }
            }
        }
        TimeSpan? idle = null;
        if (el.TryGetProperty("idleTtl", out var itEl) && itEl.ValueKind == JsonValueKind.String)
        {
            var raw = itEl.GetString();
            if (!ManifestValidation.TryParseDuration(raw!, out var parsed))
            {
                errors.Add($"{prefix}spec.autoscaling.idleTtl '{raw}' is not a valid duration");
            }
            else
            {
                idle = parsed;
            }
        }
        return new AutoscalingSpec(min, max, targetMetric) { TargetValue = targetValue, IdleTtl = idle };
    }

    private static void CheckDuplicateIds(IEnumerable<AgentManifest> manifests, List<string> errors)
    {
        var seen = new HashSet<(string Id, string Version)>();
        foreach (var m in manifests)
        {
            var key = (m.Id, m.Version);
            if (!seen.Add(key))
            {
                errors.Add($"duplicate manifest: id='{m.Id}' version='{m.Version}'");
            }
        }
    }
}
