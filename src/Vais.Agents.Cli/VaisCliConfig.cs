// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Cli;

/// <summary>
/// On-disk shape of <c>~/.vais/config.yaml</c>. Mirrors <c>kubectl</c>'s
/// config file: clusters describe how to reach a control plane, users
/// carry credentials, contexts tie one cluster to one user, and
/// <see cref="CurrentContext"/> is the active pairing.
/// </summary>
/// <remarks>
/// Shape is versioned via <see cref="ApiVersion"/> (default
/// <c>vais.io/v1</c>). Additive field changes stay at v1; incompatible
/// shape changes bump to <c>vais.io/v2</c> with dual-path load for one
/// minor release.
/// </remarks>
public sealed class VaisCliConfig
{
    /// <summary>Schema version. Locked at <c>vais.io/v1</c> for v0.15.</summary>
    public string ApiVersion { get; set; } = "vais.io/v1";

    /// <summary>Always <c>Config</c> for this file; mirrors the Kubernetes convention.</summary>
    public string Kind { get; set; } = "Config";

    /// <summary>Name of the context in <see cref="Contexts"/> that the CLI uses when no <c>--context</c> override is supplied.</summary>
    public string? CurrentContext { get; set; }

    /// <summary>Defined control-plane endpoints. Keyed by <see cref="VaisCluster.Name"/>.</summary>
    public IList<VaisCluster> Clusters { get; set; } = new List<VaisCluster>();

    /// <summary>Defined caller identities. Keyed by <see cref="VaisUser.Name"/>.</summary>
    public IList<VaisUser> Users { get; set; } = new List<VaisUser>();

    /// <summary>Named (cluster, user) pairings. Keyed by <see cref="VaisContext.Name"/>.</summary>
    public IList<VaisContext> Contexts { get; set; } = new List<VaisContext>();
}

/// <summary>Control-plane endpoint.</summary>
public sealed class VaisCluster
{
    /// <summary>Unique name (e.g. <c>local</c>, <c>prod</c>).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Base URL of the control plane (e.g. <c>https://vais.example.invalid</c>).</summary>
    public string Server { get; set; } = string.Empty;

    /// <summary>Opt-in relaxation for self-signed TLS certs — dev convenience. Never set <c>true</c> in production.</summary>
    public bool InsecureSkipTlsVerify { get; set; }
}

/// <summary>Caller identity (bearer-token credential).</summary>
public sealed class VaisUser
{
    /// <summary>Unique name (e.g. <c>dev-token</c>, <c>prod-token</c>).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Inline bearer token. Mutually exclusive with <see cref="TokenFile"/>.</summary>
    public string? Token { get; set; }

    /// <summary>Path to a file containing the bearer token — re-read per invocation so rotated tokens are picked up without config edits.</summary>
    public string? TokenFile { get; set; }
}

/// <summary>Named pairing of a cluster and a user.</summary>
public sealed class VaisContext
{
    /// <summary>Unique name (e.g. <c>default</c>, <c>production</c>).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Reference to <see cref="VaisCluster.Name"/>.</summary>
    public string Cluster { get; set; } = string.Empty;

    /// <summary>Reference to <see cref="VaisUser.Name"/>.</summary>
    public string User { get; set; } = string.Empty;
}
