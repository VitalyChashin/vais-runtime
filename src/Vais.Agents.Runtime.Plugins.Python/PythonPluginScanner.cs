// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Vais.Agents.Runtime.Plugins.Python;

/// <summary>
/// Scans a plugins root directory, parses <c>plugin.yaml</c> and
/// <c>pyproject.toml</c> in each subfolder, validates ABI, and emits
/// <see cref="PythonPluginDescriptor"/> for every successfully loaded Python plugin.
/// Non-fatal per-folder failures log a warning with the appropriate URN and
/// continue scanning remaining subfolders.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ambiguous-folder detection (Python side).</b> When a subfolder contains
/// both <c>plugin.yaml</c> (with <c>runtime: python</c>) and <c>.dll</c> files,
/// this scanner emits <see cref="PythonPluginUrns.AmbiguousFolder"/> and skips
/// the folder. The reciprocal check in <c>AssemblyPluginLoader</c> (where the
/// .NET loader also refuses the folder) is wired up in PR 3 during composition-root
/// integration.
/// </para>
/// </remarks>
internal sealed class PythonPluginScanner
{
    private readonly PythonPluginLoaderOptions _options;
    private readonly ILogger<PythonPluginScanner> _logger;
    private readonly PluginYamlDeserializer _yaml;
    private readonly PyprojectTomlReader _toml;

    internal PythonPluginScanner(
        PythonPluginLoaderOptions? options = null,
        ILogger<PythonPluginScanner>? logger = null)
    {
        _options = options ?? new PythonPluginLoaderOptions();
        _logger = logger ?? NullLogger<PythonPluginScanner>.Instance;
        _yaml = new PluginYamlDeserializer();
        _toml = new PyprojectTomlReader();
    }

    /// <summary>
    /// Scan <see cref="PythonPluginLoaderOptions.PluginsDirectory"/> and return
    /// descriptors for all successfully loaded Python plugins.
    /// </summary>
    internal IReadOnlyList<PythonPluginDescriptor> Scan()
    {
        var pluginsDirectory = _options.PluginsDirectory;

        if (!Directory.Exists(pluginsDirectory))
        {
            _logger.LogInformation(
                "Python plugins directory '{Dir}' does not exist — Python plugin loading skipped.",
                pluginsDirectory);
            return Array.Empty<PythonPluginDescriptor>();
        }

        var subfolders = Directory.GetDirectories(pluginsDirectory);
        if (subfolders.Length == 0)
        {
            _logger.LogInformation(
                "Python plugins directory '{Dir}' is empty — no Python plugins to load.",
                pluginsDirectory);
            return Array.Empty<PythonPluginDescriptor>();
        }

        var result = new List<PythonPluginDescriptor>(subfolders.Length);
        foreach (var folder in subfolders)
        {
            if (TryLoadDescriptor(folder) is { } descriptor)
                result.Add(descriptor);
        }

        _logger.LogInformation(
            "Python plugin scanning complete — {Count} plugin(s) loaded from '{Dir}'.",
            result.Count,
            pluginsDirectory);

        return result;
    }

    private PythonPluginDescriptor? TryLoadDescriptor(string folder)
    {
        var yamlPath = Path.Combine(folder, "plugin.yaml");
        if (!File.Exists(yamlPath))
            return null; // Not a plugin folder — silently skip

        string yamlContent;
        try
        {
            yamlContent = File.ReadAllText(yamlPath);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex,
                "[{Urn}] Cannot read plugin.yaml in '{Folder}'.",
                PythonPluginUrns.LoadFailed, folder);
            return null;
        }

        PluginYamlDocument? doc;
        try
        {
            doc = _yaml.Deserialize(yamlContent);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[{Urn}] YAML parse error in plugin.yaml in '{Folder}'.",
                PythonPluginUrns.LoadFailed, folder);
            return null;
        }

        if (doc?.Spec is null ||
            !string.Equals(doc.Spec.Runtime, "python", StringComparison.OrdinalIgnoreCase))
        {
            return null; // Not a Python plugin — silently skip
        }

        // Ambiguous-folder guard: Python plugin.yaml + .NET DLLs coexist.
        if (Directory.GetFiles(folder, "*.dll", SearchOption.TopDirectoryOnly).Length > 0)
        {
            _logger.LogWarning(
                "[{Urn}] Folder '{Folder}' contains both plugin.yaml (runtime: python) " +
                "and .dll files — skipping. The .NET loader will also refuse this folder (PR 3 wiring).",
                PythonPluginUrns.AmbiguousFolder, folder);
            return null;
        }

        // Parse pyproject.toml for ABI version and declared tool names.
        var tomlPath = Path.Combine(folder, "pyproject.toml");
        if (!File.Exists(tomlPath))
        {
            _logger.LogWarning(
                "[{Urn}] Python plugin in '{Folder}' has no pyproject.toml.",
                PythonPluginUrns.LoadFailed, folder);
            return null;
        }

        string tomlContent;
        try
        {
            tomlContent = File.ReadAllText(tomlPath);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex,
                "[{Urn}] Cannot read pyproject.toml in '{Folder}'.",
                PythonPluginUrns.LoadFailed, folder);
            return null;
        }

        PyprojectTomlSection? section;
        try
        {
            section = _toml.Read(tomlContent);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[{Urn}] TOML parse error in pyproject.toml in '{Folder}'.",
                PythonPluginUrns.LoadFailed, folder);
            return null;
        }

        if (section is null)
        {
            _logger.LogWarning(
                "[{Urn}] pyproject.toml in '{Folder}' has no [tool.vais.plugin] section.",
                PythonPluginUrns.LoadFailed, folder);
            return null;
        }

        // ABI check: exact match on major version string during 0.x.
        if (!string.Equals(_options.RuntimeAbiVersion, section.TargetApiVersion, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "[{Urn}] Python plugin in '{Folder}' declares targetApiVersion='{PluginAbi}' " +
                "but the runtime expects '{RuntimeAbi}' — skipping.",
                PythonPluginUrns.AbiMismatch, folder, section.TargetApiVersion, _options.RuntimeAbiVersion);
            return null;
        }

        // Resolve interpreter path (relative to plugin dir) and validate it doesn't escape.
        var interpreterRel = doc.Spec.Python?.Interpreter;
        if (string.IsNullOrWhiteSpace(interpreterRel))
            interpreterRel = ".venv/bin/python";

        var interpreterAbs = Path.GetFullPath(Path.Combine(folder, interpreterRel));
        if (!IsPathInsideDirectory(interpreterAbs, folder))
        {
            _logger.LogWarning(
                "[{Urn}] Interpreter path '{Interpreter}' in '{Folder}' resolves outside " +
                "the plugin directory — skipping.",
                PythonPluginUrns.LoadFailed, folder, interpreterRel);
            return null;
        }

        var entrypointRel = doc.Spec.Entrypoint;
        var entrypointAbs = string.IsNullOrWhiteSpace(entrypointRel)
            ? folder
            : Path.GetFullPath(Path.Combine(folder, entrypointRel));

        var pluginName = doc.Metadata?.Name is { Length: > 0 } n ? n : Path.GetFileName(folder);
        var handshakeTimeout = doc.Spec.Health is { HandshakeTimeoutSeconds: > 0 } h
            ? h.HandshakeTimeoutSeconds
            : _options.DefaultHandshakeTimeoutSeconds;
        var invokeTimeout = doc.Spec.Health is { InvokeTimeoutSeconds: > 0 } ih
            ? ih.InvokeTimeoutSeconds
            : _options.DefaultInvokeTimeoutSeconds;
        var restartPolicy = ParseRestartPolicy(doc.Spec.Health?.RestartPolicy);
        var handlerKind = ParseHandlerKind(doc.Spec.Kind);

        // Agent-handler plugins: validate the required handler.typeName field.
        string? handlerTypeName = null;
        if (handlerKind == PythonHandlerKind.AgentHandler)
        {
            handlerTypeName = doc.Spec.Handler?.TypeName;
            if (string.IsNullOrWhiteSpace(handlerTypeName))
            {
                _logger.LogWarning(
                    "[{Urn}] Python agent-handler plugin '{Name}' in '{Folder}' has " +
                    "spec.kind: agent-handler but spec.handler.typeName is missing or empty — skipping.",
                    PythonPluginUrns.LoadFailed, pluginName, folder);
                return null;
            }
        }
        else if (section.Tools.Count == 0)
        {
            _logger.LogWarning(
                "Python plugin '{Name}' in '{Folder}' declares no tools in [tool.vais.plugin].tools.",
                pluginName, folder);
        }

        return new PythonPluginDescriptor(
            Name: pluginName,
            PluginDirectory: folder,
            InterpreterPath: interpreterAbs,
            EntrypointPath: entrypointAbs,
            TargetApiVersion: section.TargetApiVersion,
            HandshakeTimeoutSeconds: handshakeTimeout,
            RestartPolicy: restartPolicy,
            DeclaredTools: section.Tools,
            SecretRefs: new Dictionary<string, string>(),
            HandlerKind: handlerKind,
            HandlerTypeName: handlerTypeName,
            InvokeTimeoutSeconds: invokeTimeout);
    }

    private static bool IsPathInsideDirectory(string path, string directory)
    {
        // Normalize both with trailing separator to avoid prefix false-positives.
        var normalizedDir = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var normalizedPath = path + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(normalizedDir, StringComparison.OrdinalIgnoreCase);
    }

    private static PythonRestartPolicy ParseRestartPolicy(string? policy) =>
        policy?.Replace("-", "").ToLowerInvariant() switch
        {
            "exponentialbackoff" => PythonRestartPolicy.ExponentialBackoff,
            _ => PythonRestartPolicy.Never,
        };

    private static PythonHandlerKind ParseHandlerKind(string? kind) =>
        kind?.Replace("-", "").ToLowerInvariant() switch
        {
            "agenthandler" => PythonHandlerKind.AgentHandler,
            _ => PythonHandlerKind.McpToolServer,
        };
}
