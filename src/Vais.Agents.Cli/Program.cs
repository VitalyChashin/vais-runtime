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
        .WithDescription("Create or update an agent from a manifest file (YAML or JSON).");

    config.AddCommand<GetAgentsCommand>("get")
        .WithDescription("List agents, or fetch a single manifest by id.")
        .WithAlias("list");

    config.AddCommand<DeleteCommand>("delete")
        .WithDescription("Evict an agent from the runtime. Prompts confirm on TTY.");

    config.AddCommand<CancelCommand>("cancel")
        .WithDescription("Cancel in-flight work on an agent; manifest + state remain.");

    config.AddCommand<InitCommand>("init")
        .WithDescription("Scaffold a starter agent-manifest YAML.");

    config.AddCommand<InvokeCommand>("invoke")
        .WithDescription("Send a user message to an agent. --stream routes via SSE.");

    config.AddCommand<LogsCommand>("logs")
        .WithDescription("Attach to an agent's live run via SSE; print events until Ctrl-C or turn.completed.");

    config.AddCommand<SignalCommand>("signal")
        .WithDescription("Send a signal (with arbitrary JSON payload) to an in-flight run.");

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
