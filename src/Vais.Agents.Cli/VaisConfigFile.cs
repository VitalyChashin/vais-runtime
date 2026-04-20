// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Vais.Agents.Cli;

/// <summary>
/// Load / save the CLI's config file. Single well-known path at
/// <c>~/.vais/config.yaml</c> on Unix and
/// <c>%USERPROFILE%\.vais\config.yaml</c> on Windows. Overridable via
/// <c>VAIS_CONFIG</c> env var.
/// </summary>
internal static class VaisConfigFile
{
    /// <summary>Environment variable that overrides <see cref="ResolveConfigPath"/>.</summary>
    public const string PathEnvVar = "VAIS_CONFIG";

    private static readonly ISerializer YamlSerializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Compute the path the CLI reads / writes. <c>VAIS_CONFIG</c> env
    /// var wins; otherwise <c>~/.vais/config.yaml</c> via
    /// <see cref="Environment.SpecialFolder.UserProfile"/>.
    /// </summary>
    public static string ResolveConfigPath()
    {
        var envOverride = Environment.GetEnvironmentVariable(PathEnvVar);
        if (!string.IsNullOrWhiteSpace(envOverride))
        {
            return envOverride;
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".vais", "config.yaml");
    }

    /// <summary>Load the config; return an empty default when the file is missing.</summary>
    public static VaisCliConfig LoadOrDefault(string? path = null)
    {
        var resolved = path ?? ResolveConfigPath();
        if (!File.Exists(resolved))
        {
            return new VaisCliConfig();
        }
        var text = File.ReadAllText(resolved);
        if (string.IsNullOrWhiteSpace(text))
        {
            return new VaisCliConfig();
        }
        return YamlDeserializer.Deserialize<VaisCliConfig>(text) ?? new VaisCliConfig();
    }

    /// <summary>Persist the config back to disk. Creates parent directories as needed.</summary>
    public static void Save(VaisCliConfig config, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        var resolved = path ?? ResolveConfigPath();
        var dir = Path.GetDirectoryName(resolved);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        var yaml = YamlSerializer.Serialize(config);
        File.WriteAllText(resolved, yaml);
    }

    /// <summary>Look up a context by name in <paramref name="config"/>. Returns null when absent.</summary>
    public static VaisContext? FindContext(VaisCliConfig config, string name)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Contexts.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));
    }

    /// <summary>Look up a cluster by name. Returns null when absent.</summary>
    public static VaisCluster? FindCluster(VaisCliConfig config, string name)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Clusters.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));
    }

    /// <summary>Look up a user by name. Returns null when absent.</summary>
    public static VaisUser? FindUser(VaisCliConfig config, string name)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Users.FirstOrDefault(u => string.Equals(u.Name, name, StringComparison.Ordinal));
    }
}
