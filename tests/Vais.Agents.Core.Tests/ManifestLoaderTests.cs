// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Control;
using Vais.Agents.Control.Manifests;
using Xunit;

namespace Vais.Agents.Core.Tests;

/// <summary>
/// v0.6 PR 2: JSON + YAML manifest loaders share a validation core. Tests cover
/// parse shape, envelope validation, required fields, K8s label-key format,
/// semver, mutually-exclusive-field rules (systemPrompt / reasoning / mcpServers),
/// SGR field-order preservation, multi-doc YAML, and duplicate-id detection.
/// </summary>
public sealed class JsonAgentManifestLoaderTests
{
    private static readonly JsonAgentManifestLoader Loader = new();

    [Fact]
    public async Task Minimal_Manifest_Round_Trips()
    {
        var json = """
        {
          "apiVersion": "vais.agents/v1",
          "kind": "Agent",
          "metadata": { "id": "echo", "version": "1.0" },
          "spec": {
            "model": { "provider": "openai", "id": "gpt-4.1-mini", "apiKeyRef": "secret://env/OPENAI_API_KEY" },
            "systemPrompt": { "inline": "Echo back." }
          }
        }
        """;

        var manifests = await Loader.LoadFromStringAsync(json);

        manifests.Should().ContainSingle();
        var m = manifests[0];
        m.Id.Should().Be("echo");
        m.Version.Should().Be("1.0");
        m.Handler.TypeName.Should().Be("declarative"); // synthesized when no handler block
        m.Model!.Provider.Should().Be("openai");
        m.SystemPrompt!.Inline.Should().Be("Echo back.");
    }

    [Fact]
    public async Task Full_Manifest_Parses_Every_Section()
    {
        var json = """
        {
          "apiVersion": "vais.agents/v1",
          "kind": "Agent",
          "metadata": {
            "id": "kb-support", "version": "1.2",
            "labels": { "team": "support", "env": "prod" },
            "annotations": { "owner": "ops@acme" }
          },
          "spec": {
            "model": { "provider": "openai", "id": "gpt-4.1", "apiKeyRef": "secret://keyvault/prod/openai-key", "temperature": 0.1 },
            "systemPrompt": { "inline": "You are a support agent." },
            "tools": [{"name":"search"},{"name":"escalate"}],
            "mcpServers": [
              { "name": "fs", "transport": "stdio", "command": "mcp-fs", "args": ["--root","/data"] }
            ],
            "guardrails": {
              "input": [{"name":"pii"}],
              "output": [{"name":"json-schema","params":{"strict":true}}]
            },
            "handoffs": [{"toAgent":"billing","when":"invoices"}],
            "budget": { "maxTurns": 5, "maxToolCalls": 10, "maxDuration": "30s" },
            "contextProviders": [{"name":"rag"}],
            "outputSchema": {"type":"object"},
            "agentMode": "toolCalling",
            "observability": { "langfuseProject": "prod", "samplingRate": 0.1 },
            "memory": { "kind": "redis", "connectionRef": "secret://env/REDIS", "scope": "session" },
            "identity": {
              "inboundAuth": "oidc",
              "inboundClaims": {"scope":"agent:invoke"},
              "outboundCredentials": [
                { "name": "openai-api", "ref": "secret://env/OPENAI_API_KEY", "type": "bearer" }
              ]
            },
            "autoscaling": { "minReplicas": 1, "maxReplicas": 5, "idleTtl": "5m" },
            "protocols": [{"kind":"Http","path":"/support"}]
          }
        }
        """;

        var m = (await Loader.LoadFromStringAsync(json))[0];

        m.Labels!["team"].Should().Be("support");
        m.Annotations!["owner"].Should().Be("ops@acme");
        m.Model!.Temperature.Should().Be(0.1);
        m.Tools.Should().HaveCount(2);
        m.McpServers!.Should().ContainSingle().Which.Command.Should().Be("mcp-fs");
        m.Guardrails!.Input!.Should().ContainSingle();
        m.Guardrails.Output!.Should().ContainSingle().Which.Params.Should().NotBeNull();
        m.Handoffs!.Should().ContainSingle().Which.When.Should().Be("invoices");
        m.Budget!.MaxTurns.Should().Be(5);
        m.Budget.MaxDuration.Should().Be(TimeSpan.FromSeconds(30));
        m.ContextProviders!.Should().ContainSingle();
        m.OutputSchema.Should().NotBeNull();
        m.Observability!.LangfuseProject.Should().Be("prod");
        m.Memory!.Scope.Should().Be("session");
        m.Identity!.Credentials!.Should().ContainSingle().Which.Type.Should().Be("bearer");
        m.Identity.InboundClaims!["scope"].Should().Be("agent:invoke");
        m.Autoscaling!.IdleTtl.Should().Be(TimeSpan.FromMinutes(5));
        m.Protocols.Should().ContainSingle().Which.Endpoint.Should().Be("/support");
    }

    [Fact]
    public async Task Missing_ApiVersion_Fails_Validation()
    {
        var json = """
        { "kind": "Agent", "metadata": { "id": "x", "version": "1" } }
        """;
        var ex = await FluentActions.Invoking(async () => await Loader.LoadFromStringAsync(json))
            .Should().ThrowAsync<AgentManifestValidationException>();
        ex.Which.Errors.Should().Contain(e => e.Contains("apiVersion"));
    }

    [Fact]
    public async Task Invalid_Id_Pattern_Fails()
    {
        var json = """
        { "apiVersion": "vais.agents/v1", "kind": "Agent",
          "metadata": { "id": "Bad_ID", "version": "1.0" } }
        """;
        var ex = await FluentActions.Invoking(async () => await Loader.LoadFromStringAsync(json))
            .Should().ThrowAsync<AgentManifestValidationException>();
        ex.Which.Errors.Should().Contain(e => e.Contains("metadata.id"));
    }

    [Fact]
    public async Task Invalid_Version_Fails()
    {
        var json = """
        { "apiVersion": "vais.agents/v1", "kind": "Agent",
          "metadata": { "id": "x", "version": "latest" } }
        """;
        var ex = await FluentActions.Invoking(async () => await Loader.LoadFromStringAsync(json))
            .Should().ThrowAsync<AgentManifestValidationException>();
        ex.Which.Errors.Should().Contain(e => e.Contains("version"));
    }

    [Fact]
    public async Task SystemPrompt_Exactly_One_Shape_Enforced()
    {
        // Zero shapes:
        var zero = """
        { "apiVersion":"vais.agents/v1","kind":"Agent",
          "metadata":{"id":"x","version":"1.0"},
          "spec":{ "systemPrompt": {} } }
        """;
        (await FluentActions.Invoking(async () => await Loader.LoadFromStringAsync(zero))
            .Should().ThrowAsync<AgentManifestValidationException>()).Which.Errors.Should().Contain(e => e.Contains("systemPrompt"));

        // Two shapes:
        var two = """
        { "apiVersion":"vais.agents/v1","kind":"Agent",
          "metadata":{"id":"x","version":"1.0"},
          "spec":{ "systemPrompt": {"inline":"a","templateRef":"b"} } }
        """;
        (await FluentActions.Invoking(async () => await Loader.LoadFromStringAsync(two))
            .Should().ThrowAsync<AgentManifestValidationException>()).Which.Errors.Should().Contain(e => e.Contains("systemPrompt"));
    }

    [Fact]
    public async Task McpServer_Command_Url_Mutually_Exclusive()
    {
        var both = """
        { "apiVersion":"vais.agents/v1","kind":"Agent",
          "metadata":{"id":"x","version":"1.0"},
          "spec":{ "mcpServers":[{"name":"n","transport":"stdio","command":"c","url":"https://x"}] } }
        """;
        (await FluentActions.Invoking(async () => await Loader.LoadFromStringAsync(both))
            .Should().ThrowAsync<AgentManifestValidationException>()).Which.Errors.Should().Contain(e => e.Contains("mcpServers"));
    }

    [Fact]
    public async Task McpServer_Plugin_Transport_Requires_No_Command_Or_Url()
    {
        // "plugin" transport → server is managed by INamedToolSourceProvider at runtime;
        // neither command nor url is required or validated.
        var json = """
        { "apiVersion":"vais.agents/v1","kind":"Agent",
          "metadata":{"id":"py-agent","version":"1.0"},
          "spec":{ "mcpServers":[{"name":"my-python-plugin","transport":"plugin","tools":["tool_a"]}] } }
        """;

        var manifests = await Loader.LoadFromStringAsync(json);

        var server = manifests[0].McpServers!.Single();
        server.Name.Should().Be("my-python-plugin");
        server.Transport.Should().Be("plugin");
        server.Command.Should().BeNull();
        server.Url.Should().BeNull();
        server.Tools.Should().ContainSingle().Which.Should().Be("tool_a");
    }

    [Fact]
    public async Task Budget_Negative_Values_Rejected()
    {
        var json = """
        { "apiVersion":"vais.agents/v1","kind":"Agent",
          "metadata":{"id":"x","version":"1.0"},
          "spec":{ "budget":{ "maxTurns": -1 } } }
        """;
        (await FluentActions.Invoking(async () => await Loader.LoadFromStringAsync(json))
            .Should().ThrowAsync<AgentManifestValidationException>()).Which.Errors.Should().Contain(e => e.Contains("maxTurns"));
    }

    [Fact]
    public async Task Autoscaling_Min_Exceeds_Max_Rejected()
    {
        var json = """
        { "apiVersion":"vais.agents/v1","kind":"Agent",
          "metadata":{"id":"x","version":"1.0"},
          "spec":{ "autoscaling":{ "minReplicas": 5, "maxReplicas": 2 } } }
        """;
        (await FluentActions.Invoking(async () => await Loader.LoadFromStringAsync(json))
            .Should().ThrowAsync<AgentManifestValidationException>()).Which.Errors.Should().Contain(e => e.Contains("minReplicas"));
    }

    [Fact]
    public async Task Json_Array_Input_Parses_Multiple_Manifests()
    {
        var json = """
        [
          { "apiVersion":"vais.agents/v1","kind":"Agent","metadata":{"id":"a","version":"1.0"}},
          { "apiVersion":"vais.agents/v1","kind":"Agent","metadata":{"id":"b","version":"1.0"}}
        ]
        """;
        var manifests = await Loader.LoadFromStringAsync(json);
        manifests.Select(m => m.Id).Should().Equal("a", "b");
    }

    [Fact]
    public async Task Duplicate_Ids_Detected()
    {
        var json = """
        [
          { "apiVersion":"vais.agents/v1","kind":"Agent","metadata":{"id":"a","version":"1.0"}},
          { "apiVersion":"vais.agents/v1","kind":"Agent","metadata":{"id":"a","version":"1.0"}}
        ]
        """;
        (await FluentActions.Invoking(async () => await Loader.LoadFromStringAsync(json))
            .Should().ThrowAsync<AgentManifestValidationException>()).Which.Errors.Should().Contain(e => e.Contains("duplicate"));
    }

    [Fact]
    public async Task Reasoning_Fields_Round_Trip_Contract_Only()
    {
        var json = """
        {
          "apiVersion":"vais.agents/v1","kind":"Agent",
          "metadata":{"id":"sgr","version":"0.1"},
          "spec":{
            "agentMode":"schemaGuidedToolCalling",
            "reasoning":{
              "pattern":"cascade",
              "schema":{"type":"object","properties":{"a":{"type":"string"},"b":{"type":"string"}}}
            }
          }
        }
        """;
        var m = (await Loader.LoadFromStringAsync(json))[0];
        m.AgentMode.Should().Be(AgentMode.SchemaGuidedToolCalling);
        m.Reasoning!.Pattern.Should().Be(ReasoningPattern.Cascade);
        m.Reasoning.Schema.Should().NotBeNull();
    }

    [Fact]
    public async Task Reasoning_Rejects_Both_Schema_And_SchemaRef()
    {
        var json = """
        { "apiVersion":"vais.agents/v1","kind":"Agent",
          "metadata":{"id":"x","version":"1.0"},
          "spec":{ "reasoning":{"pattern":"cascade","schema":{},"schemaRef":"foo"} } }
        """;
        (await FluentActions.Invoking(async () => await Loader.LoadFromStringAsync(json))
            .Should().ThrowAsync<AgentManifestValidationException>()).Which.Errors.Should().Contain(e => e.Contains("reasoning"));
    }

    [Fact]
    public async Task Reasoning_Schema_Field_Order_Preserved()
    {
        // Field order is load-bearing for SGR — the LLM fills top-to-bottom.
        var json = """
        { "apiVersion":"vais.agents/v1","kind":"Agent",
          "metadata":{"id":"x","version":"1.0"},
          "spec":{ "reasoning":{"pattern":"cascade","schema":{"type":"object","properties":{
             "first_step":{"type":"string"},
             "second_step":{"type":"string"},
             "conclusion":{"type":"string"}
          }}} } }
        """;
        var m = (await Loader.LoadFromStringAsync(json))[0];
        var schema = m.Reasoning!.Schema!.Value;
        var props = schema.GetProperty("properties");
        var keys = props.EnumerateObject().Select(p => p.Name).ToArray();
        keys.Should().Equal("first_step", "second_step", "conclusion");
    }
}

public sealed class YamlAgentManifestLoaderTests
{
    private static readonly YamlAgentManifestLoader Loader = new();

    [Fact]
    public async Task Minimal_Yaml_Round_Trips()
    {
        var yaml = """
        apiVersion: vais.agents/v1
        kind: Agent
        metadata:
          id: echo
          version: "1.0"
        spec:
          model:
            provider: openai
            id: gpt-4.1-mini
            apiKeyRef: secret://env/OPENAI_API_KEY
          systemPrompt:
            inline: |
              Echo back.
        """;
        var m = (await Loader.LoadFromStringAsync(yaml))[0];
        m.Id.Should().Be("echo");
        m.Model!.Provider.Should().Be("openai");
        m.SystemPrompt!.Inline!.TrimEnd().Should().Be("Echo back.");
    }

    [Fact]
    public async Task Multi_Document_Yaml_Is_Supported()
    {
        var yaml = """
        apiVersion: vais.agents/v1
        kind: Agent
        metadata: { id: a, version: "1.0" }
        ---
        apiVersion: vais.agents/v1
        kind: Agent
        metadata: { id: b, version: "1.0" }
        """;
        var manifests = await Loader.LoadFromStringAsync(yaml);
        manifests.Select(m => m.Id).Should().Equal("a", "b");
    }

    [Fact]
    public async Task Yaml_Key_Order_Preserved_Through_Normalisation()
    {
        // Same SGR field-order test as JSON; YAML path normalises YAML → JSON and
        // the JSON loader carries ordering via JsonDocument. End-to-end preservation.
        var yaml = """
        apiVersion: vais.agents/v1
        kind: Agent
        metadata: { id: x, version: "1.0" }
        spec:
          reasoning:
            pattern: cascade
            schema:
              type: object
              properties:
                first: { type: string }
                second: { type: string }
                conclusion: { type: string }
        """;
        var m = (await Loader.LoadFromStringAsync(yaml))[0];
        var keys = m.Reasoning!.Schema!.Value.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToArray();
        keys.Should().Equal("first", "second", "conclusion");
    }

    [Fact]
    public async Task Yaml_Booleans_Integers_Strings_Normalise_To_Correct_Json_Types()
    {
        var yaml = """
        apiVersion: vais.agents/v1
        kind: Agent
        metadata: { id: x, version: "1.0" }
        spec:
          model: { provider: openai, id: gpt, temperature: 0.2, maxTokens: 4096 }
          observability: { tracingEnabled: true, samplingRate: 0.5 }
        """;
        var m = (await Loader.LoadFromStringAsync(yaml))[0];
        m.Model!.Temperature.Should().Be(0.2);
        m.Model.MaxTokens.Should().Be(4096);
        m.Observability!.TracingEnabled.Should().BeTrue();
        m.Observability.SamplingRate.Should().Be(0.5);
    }

    [Fact]
    public async Task Yaml_Invalid_Manifest_Surfaces_Validation_Exception()
    {
        var yaml = """
        apiVersion: vais.agents/v1
        kind: Agent
        metadata: { id: Bad, version: "bad-version" }
        """;
        var ex = await FluentActions.Invoking(async () => await Loader.LoadFromStringAsync(yaml))
            .Should().ThrowAsync<AgentManifestValidationException>();
        ex.Which.Errors.Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Yaml_Load_From_File()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tmp, """
            apiVersion: vais.agents/v1
            kind: Agent
            metadata: { id: tmp, version: "1.0" }
            """);
            var manifests = await Loader.LoadFromFileAsync(tmp);
            manifests.Should().ContainSingle().Which.Id.Should().Be("tmp");
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public async Task Yaml_Load_From_Directory_Orders_Files_And_Detects_Cross_File_Duplicates()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "a.yaml"), """
                apiVersion: vais.agents/v1
                kind: Agent
                metadata: { id: a, version: "1.0" }
                """);
            await File.WriteAllTextAsync(Path.Combine(dir, "b.yaml"), """
                apiVersion: vais.agents/v1
                kind: Agent
                metadata: { id: b, version: "1.0" }
                """);
            var manifests = await Loader.LoadFromDirectoryAsync(dir, "*.yaml");
            manifests.Select(m => m.Id).Should().Equal("a", "b");

            // Introduce a duplicate and expect validation failure.
            await File.WriteAllTextAsync(Path.Combine(dir, "c.yaml"), """
                apiVersion: vais.agents/v1
                kind: Agent
                metadata: { id: a, version: "1.0" }
                """);
            var ex = await FluentActions.Invoking(async () => await Loader.LoadFromDirectoryAsync(dir, "*.yaml"))
                .Should().ThrowAsync<AgentManifestValidationException>();
            ex.Which.Errors.Should().Contain(e => e.Contains("duplicate"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task LocalAgents_Section_Parses_All_Fields()
    {
        var json = """
        {
          "apiVersion": "vais.agents/v1",
          "kind": "Agent",
          "metadata": { "id": "coordinator", "version": "1.0" },
          "spec": {
            "localAgents": [
              {
                "name": "summarizer",
                "agentId": "summarizer-agent",
                "agentVersion": "2.0",
                "description": "Summarises text",
                "allowCallerSuppliedSession": true,
                "propagateAllowedTools": false,
                "mode": "Blocking"
              }
            ],
            "tools": [{"name":"call_summarizer","source":"agent:summarizer"}]
          }
        }
        """;

        var manifests = await Loader.LoadFromStringAsync(json);
        var m = manifests.Should().ContainSingle().Subject;
        m.LocalAgents.Should().ContainSingle();
        var la = m.LocalAgents![0];
        la.Name.Should().Be("summarizer");
        la.AgentId.Should().Be("summarizer-agent");
        la.AgentVersion.Should().Be("2.0");
        la.Description.Should().Be("Summarises text");
        la.AllowCallerSuppliedSession.Should().BeTrue();
        la.PropagateAllowedTools.Should().BeFalse();
        la.Mode.Should().Be(LocalAgentInvocationMode.Blocking);
    }

    [Fact]
    public async Task LocalAgents_Minimal_Entry_Uses_Defaults()
    {
        var json = """
        {
          "apiVersion": "vais.agents/v1",
          "kind": "Agent",
          "metadata": { "id": "coordinator", "version": "1.0" },
          "spec": {
            "localAgents": [{ "name": "helper" }]
          }
        }
        """;

        var manifests = await Loader.LoadFromStringAsync(json);
        var la = manifests[0].LocalAgents!.Should().ContainSingle().Subject;
        la.Name.Should().Be("helper");
        la.AgentId.Should().BeNull();
        la.AgentVersion.Should().BeNull();
        la.Description.Should().BeNull();
        la.AllowCallerSuppliedSession.Should().BeFalse();
        la.PropagateAllowedTools.Should().BeTrue();
        la.Mode.Should().Be(LocalAgentInvocationMode.Blocking);
    }
}
