// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Vais.Agents.Control.Policy.Opa;

namespace Vais.Agents.Runtime.Host;

/// <summary>
/// Runtime-host composition inputs — derived from env vars at startup, or
/// constructed directly by unit tests. Keeping this a record lets tests
/// exercise <see cref="CompositionRoot"/> without touching process env state.
/// </summary>
internal sealed record RuntimeOptions
{
    public const string DefaultMode = "localhost";
    public const string DefaultClusteringBackend = "redis";

    /// <summary><c>localhost</c> (memory streams + storage) or <c>clustered</c>.</summary>
    public string Mode { get; init; } = DefaultMode;

    /// <summary><c>redis</c> (default) or <c>postgres</c>. Ignored in localhost mode.</summary>
    public string ClusteringBackend { get; init; } = DefaultClusteringBackend;

    public string? RedisConnection { get; init; }
    public string? PostgresConnection { get; init; }

    public string? OtelEndpoint { get; init; }
    public bool OtelConsole { get; init; }

    public string? LangfuseProject { get; init; }

    public string? OpaBaseUrl { get; init; }
    public OpaFailMode OpaFailMode { get; init; } = OpaFailMode.Closed;
    public string? OpaDataPath { get; init; }

    /// <summary>Pull the canonical shape from process env vars.</summary>
    public static RuntimeOptions FromEnvironment()
    {
        return new RuntimeOptions
        {
            Mode = Env("VAIS_HOSTING_MODE") ?? DefaultMode,
            ClusteringBackend = Env("VAIS_CLUSTERING_BACKEND") ?? DefaultClusteringBackend,
            RedisConnection = Env("VAIS_REDIS_CONNECTION"),
            PostgresConnection = Env("VAIS_POSTGRES_CONNECTION"),
            OtelEndpoint = Env("OTEL_EXPORTER_OTLP_ENDPOINT"),
            OtelConsole = string.Equals(Env("VAIS_OTEL_CONSOLE"), "true", StringComparison.OrdinalIgnoreCase),
            LangfuseProject = Env("VAIS_LANGFUSE_PROJECT"),
            OpaBaseUrl = Env("VAIS_OPA_BASEURL"),
            OpaFailMode = ParseFailMode(Env("VAIS_OPA_FAILMODE")),
            OpaDataPath = Env("VAIS_OPA_DATAPATH"),
        };

        static string? Env(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        static OpaFailMode ParseFailMode(string? raw) =>
            !string.IsNullOrWhiteSpace(raw) && Enum.TryParse<OpaFailMode>(raw, ignoreCase: true, out var parsed)
                ? parsed
                : OpaFailMode.Closed;
    }

    /// <summary>
    /// Validate mutually dependent fields. Throws <see cref="InvalidOperationException"/> with
    /// an actionable message when a required pairing is missing — for example, clustered mode
    /// without a connection string. Called before silo wiring so misconfiguration surfaces
    /// during the silo-builder callback, not at first grain invocation.
    /// </summary>
    public void EnsureValid()
    {
        if (Mode is not ("localhost" or "clustered"))
        {
            throw new InvalidOperationException(
                $"VAIS_HOSTING_MODE must be 'localhost' or 'clustered'; got '{Mode}'.");
        }

        if (Mode == "clustered")
        {
            if (ClusteringBackend is not ("redis" or "postgres"))
            {
                throw new InvalidOperationException(
                    $"VAIS_CLUSTERING_BACKEND must be 'redis' or 'postgres' in clustered mode; got '{ClusteringBackend}'.");
            }

            if (ClusteringBackend == "redis" && string.IsNullOrWhiteSpace(RedisConnection))
            {
                throw new InvalidOperationException(
                    "VAIS_REDIS_CONNECTION is required when VAIS_HOSTING_MODE=clustered and VAIS_CLUSTERING_BACKEND=redis. "
                    + "Set it to a StackExchange.Redis connection string (e.g. 'redis:6379,password=...') and retry.");
            }

            if (ClusteringBackend == "postgres" && string.IsNullOrWhiteSpace(PostgresConnection))
            {
                throw new InvalidOperationException(
                    "VAIS_POSTGRES_CONNECTION is required when VAIS_HOSTING_MODE=clustered and VAIS_CLUSTERING_BACKEND=postgres. "
                    + "Set it to an Npgsql connection string and retry.");
            }
        }
    }
}
