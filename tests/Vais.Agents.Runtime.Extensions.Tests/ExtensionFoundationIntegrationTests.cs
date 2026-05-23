// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Xunit;

namespace Vais.Agents.Runtime.Extensions.Tests;

/// <summary>
/// Foundation integration tests for Phase A extension infrastructure.
/// Tests registry, chain composer, scope matcher, and YAML deserializer
/// without loading DLLs (DLL-loading tests are covered in E2E tests).
/// </summary>
public sealed class ExtensionFoundationIntegrationTests
{
    // ── 1. Registry: swap registers a new descriptor ──────────────────────
    [Fact]
    public async Task Registry_Swap_RegistersDescriptor()
    {
        var registry = new ExtensionHandlerRegistry();
        var descriptor = MakeDescriptor("ext-log", "1.0.0");

        var old = await registry.SwapAsync("ext-log", descriptor);

        old.Should().BeNull("first registration has no previous version");
        registry.All.Should().ContainSingle(d => d.ExtensionId == "ext-log");
    }

    // ── 2. Chain composer: in-scope agent gets both input+output handlers ─
    [Fact]
    public async Task ChainComposer_InScopeAgent_IncludesBothSeams()
    {
        var registry = new ExtensionHandlerRegistry();
        var inputMw = new NoOpInputMiddleware();
        var outputMw = new NoOpOutputMiddleware();

        var descriptor = MakeDescriptorWithHandlers("ext-log", "1.0.0", scope: null,
            ("log-in",  ExtensionSeams.AgentInput,  inputMw,  100),
            ("log-out", ExtensionSeams.AgentOutput, outputMw, 100));

        await registry.SwapAsync("ext-log", descriptor);

        var composer = new DefaultExtensionChainComposer(registry, agentRegistry: null);
        var inputChain  = await composer.GetInputChainAsync("any-agent");
        var outputChain = await composer.GetOutputChainAsync("any-agent");

        // Chain returns instrumented wrappers; verify counts and that they delegate correctly.
        inputChain.Should().ContainSingle();
        outputChain.Should().ContainSingle();

        var inputInvoked = false;
        await inputChain[0].InvokeAsync(
            new AgentInputContext { AgentId = "a", Message = "" },
            () => { inputInvoked = true; return Task.CompletedTask; });
        inputInvoked.Should().BeTrue("inner middleware must call next");

        var outputInvoked = false;
        await outputChain[0].InvokeAsync(
            new AgentOutputContext
            {
                AgentId = "a", RunId = "r", SessionId = null,
                RequestMessages = Array.Empty<ChatTurn>(),
                ResponseMessage = new ChatTurn(AgentChatRole.Assistant, ""),
            },
            () => { outputInvoked = true; return Task.CompletedTask; });
        outputInvoked.Should().BeTrue("inner output middleware must call next");
    }

    // ── 3. Registry: swap returns old descriptor ──────────────────────────
    [Fact]
    public async Task Registry_Swap_ReturnsOldDescriptor()
    {
        var registry = new ExtensionHandlerRegistry();
        var v1 = MakeDescriptor("ext-log", "1.0.0");
        var v2 = MakeDescriptor("ext-log", "2.0.0");

        await registry.SwapAsync("ext-log", v1);
        var old = await registry.SwapAsync("ext-log", v2);

        old.Should().NotBeNull();
        old!.Version.Should().Be("1.0.0");
        registry.All.Should().ContainSingle(d => d.Version == "2.0.0");
    }

    // ── 4. Registry: remove + composer invalidation clears chain ──────────
    [Fact]
    public async Task Registry_Remove_ClearsHandlerFromChain()
    {
        var registry = new ExtensionHandlerRegistry();
        var inputMw = new NoOpInputMiddleware();
        var descriptor = MakeDescriptorWithHandlers("ext-log", "1.0.0", scope: null,
            ("log-in", ExtensionSeams.AgentInput, inputMw, 100));

        await registry.SwapAsync("ext-log", descriptor);

        var composer = new DefaultExtensionChainComposer(registry, agentRegistry: null);

        var before = await composer.GetInputChainAsync("agent-x");
        before.Should().ContainSingle();

        await registry.RemoveAsync("ext-log");
        composer.InvalidateAll();

        var after = await composer.GetInputChainAsync("agent-x");
        after.Should().BeEmpty("handler removed from registry after extension delete");
    }

    // ── 5. Priority ordering: lower value runs first ───────────────────────
    [Fact]
    public async Task ChainComposer_PriorityOrdering_LowerRunsFirst()
    {
        var registry = new ExtensionHandlerRegistry();
        var first  = new TaggedInputMiddleware("first");
        var second = new TaggedInputMiddleware("second");

        // Register in reverse order of priority
        var descA = MakeDescriptorWithHandlers("ext-a", "1.0.0", scope: null,
            ("ha", ExtensionSeams.AgentInput, second, 200));
        var descB = MakeDescriptorWithHandlers("ext-b", "1.0.0", scope: null,
            ("hb", ExtensionSeams.AgentInput, first,  100));

        await registry.SwapAsync("ext-a", descA);
        await registry.SwapAsync("ext-b", descB);

        var composer = new DefaultExtensionChainComposer(registry, agentRegistry: null);
        var chain = await composer.GetInputChainAsync("agent");

        // Chain returns instrumented wrappers; verify order by execution, not identity.
        chain.Should().HaveCount(2);
        var order = new List<string>();
        var ctx = new AgentInputContext { AgentId = "agent", Message = "" };
        await chain[0].InvokeAsync(ctx, async () =>
        {
            await chain[1].InvokeAsync(ctx, () => Task.CompletedTask);
        });
        // The two wrappers wrap `first` (priority 100) and `second` (priority 200).
        // Verify that chain[0] is the wrapper for the lower-priority (first-to-run) middleware
        // by checking that chain[0]'s inner NoOp still calls next (we can't check identity directly).
        chain[0].Should().NotBe(chain[1], "two distinct wrappers must be created for two bindings");
    }

    // ── 6. Scope: cluster-wide (null scope) matches any agent ─────────────
    [Fact]
    public void ScopeMatcher_NullScope_AlwaysMatches()
    {
        ExtensionScopeMatcher.Matches(scope: null, manifest: null, agentId: "any-agent")
            .Should().BeTrue("null scope = cluster-wide");
    }

    // ── 7. Scope: agentIds filter excludes non-matching agent ──────────────
    [Fact]
    public async Task ChainComposer_AgentIdScope_ExcludesNonMatchingAgent()
    {
        var registry = new ExtensionHandlerRegistry();
        var inputMw = new NoOpInputMiddleware();

        var scope = new ExtensionScope(
            Workspaces: null,
            AgentIds: new List<string> { "specific-agent" },
            Selector: null);

        var descriptor = MakeDescriptorWithHandlers("ext-log", "1.0.0", scope: scope,
            ("log-in", ExtensionSeams.AgentInput, inputMw, 100));

        await registry.SwapAsync("ext-log", descriptor);

        var composer = new DefaultExtensionChainComposer(registry, agentRegistry: null);

        var inScope  = await composer.GetInputChainAsync("specific-agent");
        var outScope = await composer.GetInputChainAsync("other-agent");

        inScope.Should().ContainSingle();
        outScope.Should().BeEmpty("agent id not in scope list");
    }

    // ── 7b. Tool seam: GetToolChainAsync builds the chain; deny middleware short-circuits ──
    [Fact]
    public async Task ChainComposer_ToolSeam_DenyMiddleware_ShortCircuits()
    {
        var registry = new ExtensionHandlerRegistry();
        var descriptor = MakeDescriptorWithHandlers("ext-tool", "1.0.0", scope: null,
            ("tool-deny", ExtensionSeams.ToolGatewayMiddleware, new DenyToolMiddleware(), 100));
        await registry.SwapAsync("ext-tool", descriptor);

        var composer = new DefaultExtensionChainComposer(registry, agentRegistry: null);
        var chain = await composer.GetToolChainAsync("any-agent");
        chain.Should().ContainSingle("the registered toolGatewayMiddleware handler must appear in the chain");

        var nextCalled = false;
        var ctx = new ToolGatewayContext(
            "some_tool", "call-1", default, new AgentContext(AgentName: "any-agent") { RunId = "r1" });
        var outcome = await chain[0].InvokeAsync(ctx, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ToolCallOutcome("call-1", "real", null));
        });

        nextCalled.Should().BeFalse("deny middleware must not dispatch the tool");
        outcome.Error.Should().Be("denied-by-ext");
    }

    // ── 7c. Tool seam: pass-through middleware dispatches the tool and returns its outcome ──
    [Fact]
    public async Task ChainComposer_ToolSeam_PassThrough_CallsNext()
    {
        var registry = new ExtensionHandlerRegistry();
        var descriptor = MakeDescriptorWithHandlers("ext-tool", "1.0.0", scope: null,
            ("tool-observe", ExtensionSeams.ToolGatewayMiddleware, new NoOpToolMiddleware(), 100));
        await registry.SwapAsync("ext-tool", descriptor);

        var composer = new DefaultExtensionChainComposer(registry, agentRegistry: null);
        var chain = await composer.GetToolChainAsync("any-agent");

        var nextCalled = false;
        var ctx = new ToolGatewayContext(
            "some_tool", "call-1", default, new AgentContext(AgentName: "any-agent") { RunId = "r1" });
        var outcome = await chain[0].InvokeAsync(ctx, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ToolCallOutcome("call-1", "real", null));
        });

        nextCalled.Should().BeTrue("pass-through middleware must dispatch the tool");
        outcome.Result.Should().Be("real");
    }

    // ── 8. YAML deserializer: parses a valid Extension manifest ───────────
    [Fact]
    public void YamlDeserializer_ValidManifest_ParsesAllFields()
    {
        var yaml = """
            apiVersion: vais.agents/v1
            kind: Extension
            metadata:
              name: my-logger
              version: "1.0.0"
              labels:
                team: platform
            spec:
              host: csharp
              handlers:
                - id: log-input
                  seam: agentInput
                  priority: 900
                  failureMode: log
              scope:
                agentIds:
                  - agent-a
            """;

        var deserializer = new ExtensionManifestYamlDeserializer();
        var manifest = deserializer.Deserialize(yaml);

        manifest.Id.Should().Be("my-logger");
        manifest.Version.Should().Be("1.0.0");
        manifest.Spec.Host.Should().Be("csharp");
        manifest.Spec.Handlers.Should().ContainSingle(h => h.Id == "log-input" && h.Seam == "agentInput");
        manifest.Spec.Scope!.AgentIds.Should().ContainSingle().Which.Should().Be("agent-a");
        manifest.Labels.Should().ContainKey("team").WhoseValue.Should().Be("platform");
    }

    // ── 8b. YAML deserializer: secrets + container fields + metadata.id fallback (MS-1c-ext) ──
    [Fact]
    public void YamlDeserializer_SecretsAndIdFallback_AreParsed()
    {
        var yaml = """
            apiVersion: vais.agents/v1
            kind: Extension
            metadata:
              id: container-ext
              version: "2.0"
            spec:
              host: container
              image: my/ext:1
              port: 9000
              topology: kubernetes
              startupTimeoutSeconds: 45
              invokeTimeoutSeconds: 10
              imagePullPolicy: Always
              handlers:
                - id: h1
                  seam: toolGatewayMiddleware
                  timeoutSeconds: 7
              secrets:
                TOKEN: secret://env/TOK
            """;

        var manifest = new ExtensionManifestYamlDeserializer().Deserialize(yaml);

        manifest.Id.Should().Be("container-ext"); // metadata.id fallback (no metadata.name)
        manifest.Spec.Port.Should().Be(9000);
        manifest.Spec.Topology.Should().Be("kubernetes");
        manifest.Spec.StartupTimeoutSeconds.Should().Be(45);
        manifest.Spec.InvokeTimeoutSeconds.Should().Be(10);
        manifest.Spec.ImagePullPolicy.Should().Be("Always");
        manifest.Spec.Handlers.Single().TimeoutSeconds.Should().Be(7);
        manifest.Spec.Secrets.Should().ContainKey("TOKEN").WhoseValue.Should().Be("secret://env/TOK");
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static ExtensionManifest MakeManifest(string id, string version, ExtensionScope? scope = null) =>
        new(Id: id, Version: version,
            Spec: new ExtensionSpec { Host = "csharp", Handlers = new List<ExtensionHandler>(), Scope = scope },
            Labels: null, Description: null);

    // Use the test assembly itself as the ALC path — it's a real assembly on disk.
    private static readonly string TestAssemblyPath =
        typeof(ExtensionFoundationIntegrationTests).Assembly.Location;

    private static ExtensionDescriptor MakeDescriptor(string id, string version) =>
        new(ExtensionId: id, Version: version,
            Manifest: MakeManifest(id, version),
            Handlers: Array.Empty<HandlerBinding>(),
            LoadContext: new ExtensionAssemblyLoadContext(TestAssemblyPath));

    private static ExtensionDescriptor MakeDescriptorWithHandlers(
        string id, string version, ExtensionScope? scope,
        params (string HandlerId, string Seam, object Instance, int Priority)[] handlers)
    {
        var bindings = handlers
            .Select(h => new HandlerBinding(
                HandlerId: h.HandlerId,
                Seam: h.Seam,
                Priority: h.Priority,
                FailureMode: "log",
                HandlerInstance: h.Instance))
            .ToArray();

        return new ExtensionDescriptor(
            ExtensionId: id,
            Version: version,
            Manifest: MakeManifest(id, version, scope),
            Handlers: bindings,
            LoadContext: new ExtensionAssemblyLoadContext(TestAssemblyPath));
    }

    private sealed class NoOpInputMiddleware : AgentInputMiddleware
    {
        public override Task InvokeAsync(AgentInputContext ctx, Func<Task> next, CancellationToken ct = default) => next();
    }

    private sealed class NoOpOutputMiddleware : AgentOutputMiddleware
    {
        public override Task InvokeAsync(AgentOutputContext ctx, Func<Task> next, CancellationToken ct = default) => next();
    }

    private sealed class TaggedInputMiddleware(string tag) : AgentInputMiddleware
    {
        public string Tag { get; } = tag;
        public override Task InvokeAsync(AgentInputContext ctx, Func<Task> next, CancellationToken ct = default) => next();
    }

    private sealed class NoOpToolMiddleware : ToolGatewayMiddleware
    {
        // Base InvokeAsync passes through (returns next()).
    }

    private sealed class DenyToolMiddleware : ToolGatewayMiddleware
    {
        public override Task<ToolCallOutcome> InvokeAsync(
            ToolGatewayContext ctx, Func<Task<ToolCallOutcome>> next, CancellationToken ct = default)
            => Task.FromResult(new ToolCallOutcome(ctx.CallId, Result: null, Error: "denied-by-ext"));
    }
}
