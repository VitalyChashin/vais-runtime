// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Xunit;

namespace Vais.Agents.Runtime.Plugins.Python.Tests;

public sealed class PythonPluginScannerTests : IDisposable
{
    // Temporary directory serving as the plugins root for each test.
    private readonly string _pluginsRoot;

    public PythonPluginScannerTests()
    {
        _pluginsRoot = Path.Combine(Path.GetTempPath(), "vais-python-scanner-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_pluginsRoot);
    }

    public void Dispose() => Directory.Delete(_pluginsRoot, recursive: true);

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private PythonPluginScanner CreateScanner(string? abiVersion = null) =>
        new(new PythonPluginLoaderOptions
        {
            PluginsDirectory = _pluginsRoot,
            RuntimeAbiVersion = abiVersion ?? PythonPluginAbi.CurrentVersion,
            DefaultHandshakeTimeoutSeconds = 5,
        });

    private string CreatePluginFolder(string name) =>
        Directory.CreateDirectory(Path.Combine(_pluginsRoot, name)).FullName;

    private static void WritePluginYaml(string folder, string content) =>
        File.WriteAllText(Path.Combine(folder, "plugin.yaml"), content);

    private static void WritePyprojectToml(string folder, string content) =>
        File.WriteAllText(Path.Combine(folder, "pyproject.toml"), content);

    private static string ValidPluginYaml(string name = "my-plugin") => $"""
        apiVersion: vais.agents/v1
        kind: Plugin
        metadata:
          name: {name}
        spec:
          runtime: python
          entrypoint: src/server.py
          python:
            version: "3.13"
            interpreter: .venv/bin/python
          health:
            handshakeTimeoutSeconds: 5
            restartPolicy: exponentialBackoff
        """;

    private const string ValidPyprojectToml = """
        [project]
        name = "my-plugin"

        [tool.vais.plugin]
        targetApiVersion = "0.24"
        tools = ["tool_a", "tool_b"]
        """;

    // ---------------------------------------------------------------------------
    // Tests
    // ---------------------------------------------------------------------------

    [Fact]
    public void Scan_NonExistentDirectory_ReturnsEmpty()
    {
        var scanner = new PythonPluginScanner(new PythonPluginLoaderOptions
        {
            PluginsDirectory = Path.Combine(_pluginsRoot, "does-not-exist"),
        });

        var result = scanner.Scan();

        result.Should().BeEmpty();
    }

    [Fact]
    public void Scan_EmptyDirectory_ReturnsEmpty()
    {
        var result = CreateScanner().Scan();

        result.Should().BeEmpty();
    }

    [Fact]
    public void Scan_FolderWithoutPluginYaml_IsSkipped()
    {
        var folder = CreatePluginFolder("not-a-plugin");
        WritePyprojectToml(folder, ValidPyprojectToml);

        var result = CreateScanner().Scan();

        result.Should().BeEmpty("no plugin.yaml means the folder is not a plugin");
    }

    [Fact]
    public void Scan_ValidPythonPlugin_ReturnsDescriptor()
    {
        var folder = CreatePluginFolder("research-planner");
        WritePluginYaml(folder, ValidPluginYaml("research-planner"));
        WritePyprojectToml(folder, ValidPyprojectToml);

        var result = CreateScanner().Scan();

        result.Should().ContainSingle();
        var d = result[0];
        d.Name.Should().Be("research-planner");
        d.PluginDirectory.Should().Be(folder);
        d.InterpreterPath.Should().Be(Path.GetFullPath(Path.Combine(folder, ".venv/bin/python")));
        d.EntrypointPath.Should().Be(Path.GetFullPath(Path.Combine(folder, "src/server.py")));
        d.TargetApiVersion.Should().Be("0.24");
        d.HandshakeTimeoutSeconds.Should().Be(5);
        d.RestartPolicy.Should().Be(PythonRestartPolicy.ExponentialBackoff);
        d.DeclaredTools.Should().BeEquivalentTo(new[] { "tool_a", "tool_b" }, o => o.WithStrictOrdering());
        d.SecretRefs.Should().BeEmpty("resolved values are populated by the runtime host, not the scanner");
        d.SecretDeclarations.Should().BeEmpty("no spec.secrets in the yaml");
    }

    [Fact]
    public void Scan_NonPythonRuntime_IsSkippedSilently()
    {
        var folder = CreatePluginFolder("node-plugin");
        WritePluginYaml(folder, """
            apiVersion: vais.agents/v1
            kind: Plugin
            metadata:
              name: node-plugin
            spec:
              runtime: node
              entrypoint: index.js
            """);
        WritePyprojectToml(folder, ValidPyprojectToml);

        var result = CreateScanner().Scan();

        result.Should().BeEmpty("node runtime is not handled by the Python scanner");
    }

    [Fact]
    public void Scan_AbiMismatch_SkipsPlugin()
    {
        var folder = CreatePluginFolder("old-plugin");
        WritePluginYaml(folder, ValidPluginYaml("old-plugin"));
        WritePyprojectToml(folder, """
            [tool.vais.plugin]
            targetApiVersion = "0.18"
            tools = ["a_tool"]
            """);

        var result = CreateScanner().Scan();

        result.Should().BeEmpty("ABI mismatch causes the plugin to be skipped");
    }

    [Fact]
    public void Scan_AmbiguousFolder_SkipsPlugin()
    {
        var folder = CreatePluginFolder("mixed-plugin");
        WritePluginYaml(folder, ValidPluginYaml("mixed-plugin"));
        WritePyprojectToml(folder, ValidPyprojectToml);
        File.WriteAllText(Path.Combine(folder, "SomeDotNetPlugin.dll"), "fake dll");

        var result = CreateScanner().Scan();

        result.Should().BeEmpty("folder with both plugin.yaml(python) and .dll is refused");
    }

    [Fact]
    public void Scan_MissingPyprojectToml_SkipsPlugin()
    {
        var folder = CreatePluginFolder("no-toml");
        WritePluginYaml(folder, ValidPluginYaml("no-toml"));

        var result = CreateScanner().Scan();

        result.Should().BeEmpty("pyproject.toml is required");
    }

    [Fact]
    public void Scan_MissingVaisPluginSection_SkipsPlugin()
    {
        var folder = CreatePluginFolder("no-section");
        WritePluginYaml(folder, ValidPluginYaml("no-section"));
        WritePyprojectToml(folder, """
            [project]
            name = "no-section"
            """);

        var result = CreateScanner().Scan();

        result.Should().BeEmpty("[tool.vais.plugin] section is required");
    }

    [Fact]
    public void Scan_InterpreterPathEscapesPluginDir_SkipsPlugin()
    {
        var folder = CreatePluginFolder("traversal-plugin");
        WritePluginYaml(folder, """
            apiVersion: vais.agents/v1
            kind: Plugin
            metadata:
              name: traversal-plugin
            spec:
              runtime: python
              entrypoint: server.py
              python:
                interpreter: ../../../usr/bin/python
            """);
        WritePyprojectToml(folder, ValidPyprojectToml);

        var result = CreateScanner().Scan();

        result.Should().BeEmpty("interpreter path escaping plugin directory is rejected");
    }

    [Fact]
    public void Scan_MalformedPluginYaml_SkipsPlugin()
    {
        var folder = CreatePluginFolder("bad-yaml");
        WritePluginYaml(folder, "spec: [\n  unclosed");
        WritePyprojectToml(folder, ValidPyprojectToml);

        var result = CreateScanner().Scan();

        result.Should().BeEmpty("malformed YAML causes the plugin to be skipped");
    }

    [Fact]
    public void Scan_MalformedPyprojectToml_SkipsPlugin()
    {
        var folder = CreatePluginFolder("bad-toml");
        WritePluginYaml(folder, ValidPluginYaml("bad-toml"));
        WritePyprojectToml(folder, "[[invalid\nkey = val");

        var result = CreateScanner().Scan();

        result.Should().BeEmpty("malformed TOML causes the plugin to be skipped");
    }

    [Fact]
    public void Scan_MultiplePlugins_LoadsAll()
    {
        for (var i = 1; i <= 3; i++)
        {
            var folder = CreatePluginFolder($"plugin-{i}");
            WritePluginYaml(folder, ValidPluginYaml($"plugin-{i}"));
            WritePyprojectToml(folder, ValidPyprojectToml);
        }

        var result = CreateScanner().Scan();

        result.Should().HaveCount(3);
    }

    [Fact]
    public void Scan_OneFailingPluginAmongMany_LoadsRemainingPlugins()
    {
        // Good plugin
        var good = CreatePluginFolder("good-plugin");
        WritePluginYaml(good, ValidPluginYaml("good-plugin"));
        WritePyprojectToml(good, ValidPyprojectToml);

        // Bad plugin (missing pyproject.toml)
        var bad = CreatePluginFolder("bad-plugin");
        WritePluginYaml(bad, ValidPluginYaml("bad-plugin"));

        var result = CreateScanner().Scan();

        result.Should().ContainSingle(d => d.Name == "good-plugin",
            "a failing plugin does not prevent other plugins from loading");
    }

    [Fact]
    public void Scan_FolderNameUsedAsPluginNameWhenMetadataNameAbsent()
    {
        var folder = CreatePluginFolder("fallback-name");
        WritePluginYaml(folder, """
            apiVersion: vais.agents/v1
            kind: Plugin
            spec:
              runtime: python
              entrypoint: server.py
            """);
        WritePyprojectToml(folder, ValidPyprojectToml);

        var result = CreateScanner().Scan();

        result.Should().ContainSingle().Which.Name.Should().Be("fallback-name");
    }

    [Fact]
    public void Scan_HandshakeTimeoutFromOptions_UsedWhenYamlOmitsHealth()
    {
        var folder = CreatePluginFolder("no-health");
        WritePluginYaml(folder, """
            apiVersion: vais.agents/v1
            kind: Plugin
            metadata:
              name: no-health
            spec:
              runtime: python
              entrypoint: server.py
            """);
        WritePyprojectToml(folder, ValidPyprojectToml);

        var scanner = new PythonPluginScanner(new PythonPluginLoaderOptions
        {
            PluginsDirectory = _pluginsRoot,
            DefaultHandshakeTimeoutSeconds = 12,
        });

        var result = scanner.Scan();

        result.Should().ContainSingle().Which.HandshakeTimeoutSeconds.Should().Be(12);
    }

    // -----------------------------------------------------------------------
    // v0.31 — spec.secrets parsing
    // -----------------------------------------------------------------------

    [Fact]
    public void Scan_PluginWithSecrets_PopulatesSecretDeclarations()
    {
        var folder = CreatePluginFolder("secrets-plugin");
        WritePluginYaml(folder, $"""
            {ValidPluginYaml("secrets-plugin")}
              secrets:
                MY_API_KEY: "secret://env/OPENAI_API_KEY"
                DB_PASS: "secret://file//run/secrets/db"
            """);
        WritePyprojectToml(folder, ValidPyprojectToml);

        var result = CreateScanner().Scan();

        result.Should().ContainSingle();
        var d = result[0];
        d.SecretDeclarations.Should().HaveCount(2);
        d.SecretDeclarations["MY_API_KEY"].Should().Be("secret://env/OPENAI_API_KEY");
        d.SecretDeclarations["DB_PASS"].Should().Be("secret://file//run/secrets/db");
        d.SecretRefs.Should().BeEmpty("resolution happens at host startup, not scan time");
    }

    [Fact]
    public void Scan_PluginWithInvalidSecretRefName_IsSkipped()
    {
        var folder = CreatePluginFolder("bad-secret-name");
        WritePluginYaml(folder, $"""
            {ValidPluginYaml("bad-secret-name")}
              secrets:
                "invalid name with spaces": "secret://env/FOO"
            """);
        WritePyprojectToml(folder, ValidPyprojectToml);

        var result = CreateScanner().Scan();

        result.Should().BeEmpty("invalid ref name must cause the plugin to be skipped");
    }

    [Fact]
    public void Scan_PluginWithRefNameStartingWithDigit_IsSkipped()
    {
        var folder = CreatePluginFolder("bad-digit-ref");
        WritePluginYaml(folder, $"""
            {ValidPluginYaml("bad-digit-ref")}
              secrets:
                123BAD: "secret://env/FOO"
            """);
        WritePyprojectToml(folder, ValidPyprojectToml);

        var result = CreateScanner().Scan();

        result.Should().BeEmpty("ref name starting with digit is invalid");
    }

    [Fact]
    public void Scan_PluginWithUnderscoreLeadRefName_IsAccepted()
    {
        var folder = CreatePluginFolder("underscore-ref");
        WritePluginYaml(folder, $"""
            {ValidPluginYaml("underscore-ref")}
              secrets:
                _PRIVATE_KEY: "secret://env/PRIVATE_KEY"
            """);
        WritePyprojectToml(folder, ValidPyprojectToml);

        var result = CreateScanner().Scan();

        result.Should().ContainSingle();
        result[0].SecretDeclarations.Should().ContainKey("_PRIVATE_KEY");
    }
}
