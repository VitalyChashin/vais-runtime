// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Vais.Agents.Cli.Commands.Config;

/// <summary>
/// Creates or updates a context. Accepts <c>--server</c> +
/// <c>--token</c> / <c>--token-file</c> inline for a one-liner setup;
/// implicit cluster + user records named after the context.
/// </summary>
/// <remarks>
/// Example: <c>vais config set-context default --server http://localhost:5080 --token demo</c>
/// creates/updates a cluster named <c>default</c>, a user named
/// <c>default</c>, and a context named <c>default</c> tying the two.
/// </remarks>
internal sealed class SetContextCommand : Command<SetContextCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Name of the context (also used for the implicit cluster + user records).")]
        [CommandArgument(0, "<name>")]
        public required string Name { get; init; }

        [Description("Base URL of the control plane (e.g. http://localhost:5080).")]
        [CommandOption("--server")]
        public string? Server { get; init; }

        [Description("Inline bearer token.")]
        [CommandOption("--token")]
        public string? Token { get; init; }

        [Description("Path to a file containing the bearer token. Re-read per invocation so rotated tokens are picked up.")]
        [CommandOption("--token-file")]
        public string? TokenFile { get; init; }

        [Description("Skip TLS certificate verification for this cluster. Dev-only.")]
        [CommandOption("--insecure-skip-tls-verify")]
        public bool InsecureSkipTlsVerify { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var config = VaisConfigFile.LoadOrDefault();

        var cluster = VaisConfigFile.FindCluster(config, settings.Name);
        if (cluster is null)
        {
            cluster = new VaisCluster { Name = settings.Name };
            config.Clusters.Add(cluster);
        }
        if (!string.IsNullOrWhiteSpace(settings.Server))
        {
            cluster.Server = settings.Server;
        }
        if (settings.InsecureSkipTlsVerify)
        {
            cluster.InsecureSkipTlsVerify = true;
        }

        var user = VaisConfigFile.FindUser(config, settings.Name);
        if (user is null)
        {
            user = new VaisUser { Name = settings.Name };
            config.Users.Add(user);
        }
        if (!string.IsNullOrWhiteSpace(settings.Token))
        {
            user.Token = settings.Token;
            user.TokenFile = null;
        }
        if (!string.IsNullOrWhiteSpace(settings.TokenFile))
        {
            user.TokenFile = settings.TokenFile;
            user.Token = null;
        }

        var ctx = VaisConfigFile.FindContext(config, settings.Name);
        if (ctx is null)
        {
            ctx = new VaisContext { Name = settings.Name, Cluster = settings.Name, User = settings.Name };
            config.Contexts.Add(ctx);
        }
        else
        {
            ctx.Cluster = settings.Name;
            ctx.User = settings.Name;
        }

        VaisConfigFile.Save(config);
        AnsiConsole.MarkupLine($"Context [bold]'{settings.Name}'[/] set.");
        return 0;
    }
}
