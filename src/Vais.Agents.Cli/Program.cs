// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Spectre.Console.Cli;
using Vais.Agents.Cli.Commands;
using Vais.Agents.Cli.Commands.Config;

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

    config.AddCommand<LlmGatewayValidateCommand>("llm-gateway-validate")
        .WithDescription("Validate an LLM gateway config manifest without registering it.");

    config.AddCommand<McpGatewayValidateCommand>("mcp-gateway-validate")
        .WithDescription("Validate an MCP gateway config manifest without registering it.");

    config.AddCommand<McpServerValidateCommand>("mcp-server-validate")
        .WithDescription("Validate an MCP server manifest without registering it.");

    config.AddCommand<LogsCommand>("logs")
        .WithDescription("Attach to an agent's live run via SSE; print events until Ctrl-C or turn.completed.");

    config.AddCommand<GraphLogsCommand>("graph-logs")
        .WithDescription("Stream graph run events via SSE. --interrupt-id resumes an interrupted run for observation.");

    config.AddCommand<SignalCommand>("signal")
        .WithDescription("Send a signal (with arbitrary JSON payload) to an in-flight run.");

    config.AddCommand<GetRemoteRuntimesCommand>("get-remote-runtimes")
        .WithDescription("List remote runtimes configured on the target host.");

    config.AddCommand<PluginStatusCommand>("plugin-status")
        .WithDescription("List loaded plugins (assembly + Python) with their lifecycle state, handlers, and PID.");

    config.AddCommand<PluginPushCommand>("plugin-push")
        .WithDescription("Pack a Python plugin's source directory and hot-reload it in the runtime.");

    config.AddCommand<PluginWatchCommand>("plugin-watch")
        .WithDescription("Watch a Python plugin's source directory and hot-reload on every change.");

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
