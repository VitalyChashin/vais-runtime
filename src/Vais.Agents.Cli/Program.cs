// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Spectre.Console.Cli;
using Vais.Agents.Cli.Commands;
using Vais.Agents.Cli.Commands.Config;
using Vais.Agents.Cli.Commands.Diagnose;

var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("vais");

    config.AddCommand<VersionCommand>("version")
        .WithDescription("Print the CLI version.");

    config.AddCommand<ApplyCommand>("apply")
        .WithDescription("Create or update agent / graph manifests from a file (YAML or JSON). Mixed-kind files supported.");

    config.AddCommand<GetAgentsCommand>("get")
        .WithDescription("List agents, or fetch a single manifest by id.")
        .WithAlias("list");

    config.AddCommand<GetGraphsCommand>("get-graphs")
        .WithDescription("List graphs, or fetch a single graph manifest by id.");

    config.AddCommand<DeleteCommand>("delete")
        .WithDescription("Evict an agent from the runtime. Prompts confirm on TTY.");

    config.AddCommand<DeleteGraphCommand>("delete-graph")
        .WithDescription("Evict a graph from the runtime. Prompts confirm on TTY.");

    config.AddCommand<CancelCommand>("cancel")
        .WithDescription("Cancel in-flight work on an agent; manifest + state remain.");

    config.AddCommand<InitCommand>("init")
        .WithDescription("Scaffold a starter agent-manifest YAML.");

    config.AddCommand<InvokeCommand>("invoke")
        .WithDescription("Send a user message to an agent. --stream routes via SSE.");

    config.AddCommand<InvokeGraphCommand>("invoke-graph")
        .WithDescription("Invoke a graph run. --stream routes via SSE. --resume-from resumes an interrupted run.");

    config.AddCommand<GraphValidateCommand>("graph-validate")
        .WithDescription("Validate a graph manifest against the runtime without registering it.");

    config.AddCommand<GetLlmGatewaysCommand>("get-llm-gateways")
        .WithDescription("List LLM gateway configs, or fetch a single config manifest by id.");

    config.AddCommand<GetMcpGatewaysCommand>("get-mcp-gateways")
        .WithDescription("List MCP gateway configs, or fetch a single config manifest by id.");

    config.AddCommand<GetMcpServersCommand>("get-mcp-servers")
        .WithDescription("List MCP server manifests, or fetch a single manifest by id.");

    config.AddCommand<GetEvalSuitesCommand>("get-eval-suites")
        .WithDescription("List eval suite manifests, or fetch a single manifest by id.");

    config.AddBranch("eval", branch =>
    {
        branch.SetDescription("Run, inspect, and cancel eval suite runs.");

        branch.AddCommand<EvalRunCommand>("run")
            .WithDescription("Start a new eval run for a named suite. Prints the evalRunId.");

        branch.AddCommand<EvalResultsCommand>("results")
            .WithDescription("Fetch and display per-case assertion results for an eval run.");

        branch.AddCommand<EvalListCommand>("list")
            .WithDescription("List recent eval runs, optionally filtered by suite name.");

        branch.AddCommand<EvalCancelCommand>("cancel")
            .WithDescription("Request cancellation of an in-progress eval run.");

        branch.AddCommand<EvalDiffCommand>("diff")
            .WithDescription("Compare two eval runs case-by-case and show assertion deltas.");
    });

    config.AddBranch("approvals", branch =>
    {
        branch.SetDescription("List and decide high-risk mutation approval requests.");

        branch.AddCommand<ApprovalListCommand>("list")
            .WithDescription("List approval requests, optionally filtered by status.");

        branch.AddCommand<ApprovalApproveCommand>("approve")
            .WithDescription("Approve a pending high-risk mutation by request id.");

        branch.AddCommand<ApprovalRejectCommand>("reject")
            .WithDescription("Reject a pending high-risk mutation by request id.");
    });

    config.AddCommand<LlmGatewayValidateCommand>("llm-gateway-validate")
        .WithDescription("Validate an LLM gateway config manifest without registering it.");

    config.AddCommand<McpGatewayValidateCommand>("mcp-gateway-validate")
        .WithDescription("Validate an MCP gateway config manifest without registering it.");

    config.AddCommand<McpServerValidateCommand>("mcp-server-validate")
        .WithDescription("Validate an MCP server manifest without registering it.");

    config.AddCommand<LogsCommand>("logs")
        .WithDescription("Attach to an agent's live run via SSE; print events until Ctrl-C or turn.completed.");

    config.AddCommand<GraphLogsCommand>("graph-logs")
        .WithDescription("Stream graph run events via SSE. --from-run-id shows stored history instead of streaming.");

    config.AddCommand<GetRunsCommand>("get-runs")
        .WithDescription("List historical graph runs or inspect a specific run's node executions from the run store.");

    config.AddBranch("trajectories", t =>
    {
        t.SetDescription("Plan D trajectory tee — query the trajectory event corpus that drives recipe induction.");
        t.AddCommand<TrajectoriesListCommand>("list")
            .WithDescription("List trajectory events (newest first). Filters: --agent --run --concept --transport --outcome --since --until --limit.");
    });

    config.AddBranch("recipes", r =>
    {
        r.SetDescription("Plan D induced recipes — propose, triage, and decide candidate authoring recipes.");
        r.AddCommand<RecipesListCommand>("list")
            .WithDescription("List induced recipe proposals (newest first). Filters: --concept --kind --status --risk --limit.");
        r.AddCommand<RecipesShowCommand>("show")
            .WithDescription("Show a single proposal by id (full body, support, confidence, risk, source traces).");
        r.AddCommand<RecipesProposeCommand>("propose")
            .WithDescription("Run the induction pipeline now and persist any new proposals. Optional corpus filters: --agent --run --concept --transport --since --until.");
        r.AddCommand<RecipesDecideCommand>("approve")
            .WithDescription("Approve a pending proposal. High-risk proposals route through the existing IApprovalStore — operator must run 'vais approvals approve <id>' first.")
            .WithData(true);
        r.AddCommand<RecipesDecideCommand>("reject")
            .WithDescription("Reject a pending proposal. Always allowed; bypasses the approval gate.")
            .WithData(false);
    });

    config.AddCommand<SignalCommand>("signal")
        .WithDescription("Send a signal (with arbitrary JSON payload) to an in-flight run.");

    config.AddCommand<GetRemoteRuntimesCommand>("get-remote-runtimes")
        .WithDescription("List remote runtimes configured on the target host.");

    config.AddCommand<PluginStatusCommand>("plugin-status")
        .WithDescription("List loaded plugins (assembly, Python, container) with lifecycle state, image, handlers, and PID.");

    config.AddCommand<PluginPushCommand>("plugin-push")
        .WithDescription("Push a plugin to the runtime. Source mode: packs ./src and hot-reloads. Image mode: docker push + POST /v1/plugins/{name}/image.");

    config.AddCommand<PluginBuildCommand>("plugin-build")
        .WithDescription("Build a container plugin image via 'docker build'. Pass --push to also push to the registry.");

    config.AddCommand<PluginDeployCommand>("plugin-deploy")
        .WithDescription("Deploy a container plugin to Kubernetes using the built-in Helm chart (helm upgrade --install).");

    config.AddCommand<PluginInitCommand>("plugin-init")
        .WithDescription("Scaffold a plugin.yaml (and Dockerfile for dotnet) in the current directory.");

    config.AddCommand<PluginWatchCommand>("plugin-watch")
        .WithDescription("Watch a Python plugin's source directory and hot-reload on every change.");

    config.AddCommand<PluginImportExistingCommand>("plugin-import-existing")
        .WithDescription("Load (or hot-reload) a plugin whose DLL is already in the runtime's plugins directory.");

    config.AddBranch("ext", branch =>
    {
        branch.SetDescription("List and inspect loaded extensions.");

        branch.AddCommand<ExtListCommand>("list")
            .WithDescription("List all loaded extensions with host, version, and handler summary.");

        branch.AddCommand<ExtGetCommand>("get")
            .WithDescription("Fetch a single loaded extension by id (full manifest + handler details).");

        branch.AddCommand<ExtLogsCommand>("logs")
            .WithDescription("Show container extension logs (host:container only; redirects to docker/kubectl).");

        branch.AddCommand<ExtMetricsCommand>("metrics")
            .WithDescription("Show per-handler latency metrics (p50/p95) for a loaded extension.");
    });

    config.AddBranch("agent", branch =>
    {
        branch.SetDescription("Agent diagnostics and inspection.");

        branch.AddCommand<AgentExtensionsCommand>("extensions")
            .WithDescription("List extension handlers bound to an agent with scope match diagnostics.");
    });

    config.AddBranch("diagnose", branch =>
    {
        branch.SetDescription("Runtime diagnostics: spans, traces, filter counters.");

        branch.AddCommand<DiagnoseSpansCommand>("spans")
            .WithDescription("Fetch recent OTel spans from the in-process buffer as NDJSON. Requires VAIS_DIAG_SPAN_BUFFER=true.");

        branch.AddCommand<DiagnoseTraceCommand>("trace")
            .WithDescription("Pretty-print a span tree for a given trace ID.");

        branch.AddCommand<DiagnoseFilterStatusCommand>("filter-status")
            .WithDescription("Show per-interface outgoing Orleans grain call counters.");
    });

    config.AddBranch("config", branch =>
    {
        branch.SetDescription("Inspect and mutate the ~/.vais/config.yaml file.");

        branch.AddCommand<GetContextsCommand>("get-contexts")
            .WithDescription("List configured contexts.");

        branch.AddCommand<CurrentContextCommand>("current-context")
            .WithDescription("Print the active context name.");

        branch.AddCommand<UseContextCommand>("use-context")
            .WithDescription("Switch the active context.");

        branch.AddCommand<SetContextCommand>("set-context")
            .WithDescription("Create or update a context.");
    });
});

return app.Run(args);
