// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Vais.Agents.Control;
using Vais.Agents.Control.Manifests;
using Xunit;

namespace Vais.Agents.Core.Tests;

/// <summary>
/// E1 — eval suite manifest parser: happy-path and validation-error coverage.
/// </summary>
public sealed class EvalSuiteManifestParserTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    private static async Task<IReadOnlyList<ManifestResource>> LoadAsync(string json)
    {
        var loader = new JsonAgentGraphManifestLoader();
        return await loader.LoadAllResourcesFromStringAsync(json, default).ConfigureAwait(false);
    }

    private static string MinimalSuite(string id = "my-suite", string version = "1.0") => $$"""
        {
          "apiVersion": "vais.agents/v1",
          "kind": "EvalSuite",
          "metadata": { "id": "{{id}}", "version": "{{version}}" },
          "spec": {
            "agentId": "my-agent",
            "cases": [
              { "id": "case-1", "input": "hello" }
            ]
          }
        }
        """;

    // ── 1. Happy-path: minimal manifest ───────────────────────────────────────

    [Fact]
    public async Task Minimal_Manifest_Parses_Into_EvalSuiteCase()
    {
        var resources = await LoadAsync(MinimalSuite());

        resources.Should().ContainSingle();
        var suiteCase = resources[0].Should().BeOfType<ManifestResource.EvalSuiteCase>().Subject;
        var m = suiteCase.Suite;
        m.Id.Should().Be("my-suite");
        m.Version.Should().Be("1.0");
        m.Spec.AgentId.Should().Be("my-agent");
        m.Spec.GraphId.Should().BeNull();
        m.Spec.Cases.Should().ContainSingle(c => c.Id == "case-1" && c.Input == "hello");
        m.Spec.ReplayMode.Should().Be(EvalReplayMode.Live);
    }

    // ── 2. Happy-path: full manifest with all optional fields ─────────────────

    [Fact]
    public async Task Full_Manifest_Parses_All_Optional_Fields()
    {
        var json = """
            {
              "apiVersion": "vais.agents/v1",
              "kind": "EvalSuite",
              "metadata": {
                "id": "full-suite",
                "version": "2.0",
                "description": "A comprehensive test suite",
                "labels": { "team": "platform", "env": "ci" }
              },
              "spec": {
                "agentId": "qa-agent",
                "replayMode": "cached",
                "defaults": { "judgeModel": "gpt-4o-judge", "timeout": "00:02:00" },
                "baseline": { "runId": "550e8400-e29b-41d4-a716-446655440000" },
                "cases": [
                  {
                    "id": "case-a",
                    "name": "Basic greeting",
                    "description": "Checks the agent handles greetings",
                    "input": "Hello",
                    "expectedOutput": "Hi there!",
                    "assertions": [
                      { "kind": "contains", "params": { "text": "Hi" } }
                    ]
                  }
                ]
              }
            }
            """;

        var resources = await LoadAsync(json);

        var m = resources.OfType<ManifestResource.EvalSuiteCase>().Single().Suite;
        m.Description.Should().Be("A comprehensive test suite");
        m.Labels.Should().ContainKey("team").WhoseValue.Should().Be("platform");
        m.Spec.ReplayMode.Should().Be(EvalReplayMode.Cached);
        m.Spec.Defaults!.JudgeModel.Should().Be("gpt-4o-judge");
        m.Spec.Defaults.Timeout.Should().Be(TimeSpan.FromMinutes(2));
        m.Spec.Baseline!.RunId.Should().Be("550e8400-e29b-41d4-a716-446655440000");

        var c = m.Spec.Cases.Single();
        c.Name.Should().Be("Basic greeting");
        c.ExpectedOutput.Should().Be("Hi there!");
        c.Assertions.Should().ContainSingle(a => a.Kind == "contains");
    }

    // ── 3. Validation error: missing metadata.id ──────────────────────────────

    [Fact]
    public async Task Missing_Id_Throws_Validation_Exception()
    {
        var json = """
            {
              "apiVersion": "vais.agents/v1",
              "kind": "EvalSuite",
              "metadata": { "version": "1.0" },
              "spec": {
                "cases": [{ "id": "c1", "input": "test" }]
              }
            }
            """;

        var act = () => LoadAsync(json);
        await act.Should().ThrowAsync<AgentManifestValidationException>();
    }

    // ── 4. Validation error: missing spec.cases ───────────────────────────────

    [Fact]
    public async Task Missing_Cases_Throws_Validation_Exception()
    {
        var json = """
            {
              "apiVersion": "vais.agents/v1",
              "kind": "EvalSuite",
              "metadata": { "id": "no-cases", "version": "1.0" },
              "spec": { "agentId": "x" }
            }
            """;

        var act = () => LoadAsync(json);
        await act.Should().ThrowAsync<AgentManifestValidationException>();
    }

    // ── 5. Validation error: empty cases array ────────────────────────────────

    [Fact]
    public async Task Empty_Cases_Array_Throws_Validation_Exception()
    {
        var json = """
            {
              "apiVersion": "vais.agents/v1",
              "kind": "EvalSuite",
              "metadata": { "id": "empty-cases", "version": "1.0" },
              "spec": { "cases": [] }
            }
            """;

        var act = () => LoadAsync(json);
        await act.Should().ThrowAsync<AgentManifestValidationException>();
    }

    // ── 6. Validation error: agentId and graphId are mutually exclusive ────────

    [Fact]
    public async Task Both_AgentId_And_GraphId_Throws_Validation_Exception()
    {
        var json = """
            {
              "apiVersion": "vais.agents/v1",
              "kind": "EvalSuite",
              "metadata": { "id": "conflict-target", "version": "1.0" },
              "spec": {
                "agentId": "agent-a",
                "graphId": "graph-b",
                "cases": [{ "id": "c1", "input": "x" }]
              }
            }
            """;

        var act = () => LoadAsync(json);
        await act.Should().ThrowAsync<AgentManifestValidationException>();
    }

    // ── 7. Validation error: duplicate case ids ───────────────────────────────

    [Fact]
    public async Task Duplicate_Case_Ids_Throw_Validation_Exception()
    {
        var json = """
            {
              "apiVersion": "vais.agents/v1",
              "kind": "EvalSuite",
              "metadata": { "id": "dup-cases", "version": "1.0" },
              "spec": {
                "cases": [
                  { "id": "c1", "input": "a" },
                  { "id": "c1", "input": "b" }
                ]
              }
            }
            """;

        var act = () => LoadAsync(json);
        await act.Should().ThrowAsync<AgentManifestValidationException>();
    }

    // ── 8. Multi-kind file: Agent + EvalSuite coexist ─────────────────────────

    [Fact]
    public async Task Multi_Kind_File_Parses_Both_Agent_And_EvalSuite()
    {
        var json = """
            [
              {
                "apiVersion": "vais.agents/v1",
                "kind": "Agent",
                "metadata": { "id": "my-agent", "version": "1.0" },
                "spec": { "model": { "provider": "openai", "id": "gpt-4o" } }
              },
              {
                "apiVersion": "vais.agents/v1",
                "kind": "EvalSuite",
                "metadata": { "id": "my-suite", "version": "1.0" },
                "spec": {
                  "agentId": "my-agent",
                  "cases": [{ "id": "c1", "input": "ping" }]
                }
              }
            ]
            """;

        var resources = await LoadAsync(json);

        resources.Should().HaveCount(2);
        resources.Should().ContainSingle(r => r is ManifestResource.AgentCase);
        resources.Should().ContainSingle(r => r is ManifestResource.EvalSuiteCase);
    }

    // ── 9. ReplayMode parsing — current names + backward-compat aliases ────────

    [Theory]
    [InlineData("live", EvalReplayMode.Live)]
    [InlineData("cached", EvalReplayMode.Cached)]
    [InlineData("none", EvalReplayMode.Live)]     // backward-compat alias
    [InlineData("record", EvalReplayMode.Cached)] // backward-compat alias
    [InlineData("replay", EvalReplayMode.Cached)] // backward-compat alias
    [InlineData("Live", EvalReplayMode.Live)]
    [InlineData("Cached", EvalReplayMode.Cached)]
    public async Task ReplayMode_Parsed_Case_Insensitively(string input, EvalReplayMode expected)
    {
        var json = $$"""
            {
              "apiVersion": "vais.agents/v1",
              "kind": "EvalSuite",
              "metadata": { "id": "rm-suite", "version": "1.0" },
              "spec": {
                "replayMode": "{{input}}",
                "cases": [{ "id": "c1", "input": "x" }]
              }
            }
            """;

        var resources = await LoadAsync(json);
        var m = resources.OfType<ManifestResource.EvalSuiteCase>().Single().Suite;
        m.Spec.ReplayMode.Should().Be(expected);
    }

    // ── 10. Validation error: case missing required input ─────────────────────

    [Fact]
    public async Task Case_Missing_Input_Throws_Validation_Exception()
    {
        var json = """
            {
              "apiVersion": "vais.agents/v1",
              "kind": "EvalSuite",
              "metadata": { "id": "bad-case", "version": "1.0" },
              "spec": {
                "cases": [{ "id": "c1" }]
              }
            }
            """;

        var act = () => LoadAsync(json);
        await act.Should().ThrowAsync<AgentManifestValidationException>();
    }

    // ── 11. Happy-path: spec.target block with agentVersion ───────────────────

    [Fact]
    public async Task Target_Block_With_AgentVersion_Parses()
    {
        var json = """
            {
              "apiVersion": "vais.agents/v1",
              "kind": "EvalSuite",
              "metadata": { "id": "versioned-suite", "version": "1.0" },
              "spec": {
                "target": { "agentRef": "my-agent", "agentVersion": "3" },
                "cases": [{ "id": "c1", "input": "ping" }]
              }
            }
            """;

        var resources = await LoadAsync(json);
        var m = resources.OfType<ManifestResource.EvalSuiteCase>().Single().Suite;
        m.Spec.Target.Should().NotBeNull();
        m.Spec.Target!.AgentRef.Should().Be("my-agent");
        m.Spec.Target.AgentVersion.Should().Be("3");
        m.Spec.AgentId.Should().Be("my-agent");
    }

    // ── 12. Happy-path: per-case replay + initialHistory ─────────────────────

    [Fact]
    public async Task Case_Replay_And_InitialHistory_Parse()
    {
        var json = """
            {
              "apiVersion": "vais.agents/v1",
              "kind": "EvalSuite",
              "metadata": { "id": "hist-suite", "version": "1.0" },
              "spec": {
                "agentId": "my-agent",
                "cases": [
                  {
                    "id": "c1",
                    "input": "what was the context?",
                    "replay": "cached",
                    "initialHistory": [
                      { "role": "user", "content": "Tell me about Orleans." },
                      { "role": "assistant", "content": "Orleans is a virtual actor framework." }
                    ]
                  }
                ]
              }
            }
            """;

        var resources = await LoadAsync(json);
        var c = resources.OfType<ManifestResource.EvalSuiteCase>().Single().Suite.Spec.Cases.Single();
        c.Replay.Should().Be(EvalReplayMode.Cached);
        c.InitialHistory.Should().HaveCount(2);
        c.InitialHistory![0].Role.Should().Be("user");
        c.InitialHistory[1].Role.Should().Be("assistant");
    }
}
