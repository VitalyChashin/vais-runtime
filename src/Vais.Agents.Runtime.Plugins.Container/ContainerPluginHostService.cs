// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net.Http.Json;
using Docker.DotNet;
using k8s;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vais.Agents.Runtime.Plugins;

namespace Vais.Agents.Runtime.Plugins.Container;

/// <summary>
/// Hosted service that scans for <c>plugin.yaml</c> files with <c>runtime: container</c>,
/// starts each container, validates ABI via <c>GET /v1/metadata</c>, and registers
/// a <see cref="ContainerAgentShimFactory"/> for each successfully started plugin.
/// </summary>
internal sealed class ContainerPluginHostService : IHostedService, IContainerPluginHost
{
    private static readonly int MaxParallelism = Math.Min(Environment.ProcessorCount, 4);

    private readonly ContainerPluginLoaderOptions _options;
    private readonly IPluginHandlerRegistry _registry;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ContainerPluginHostService> _logger;
    private readonly List<IContainerSupervisor> _supervisors = new();

    public IReadOnlyList<LoadedContainerPlugin> LoadedPlugins
    {
        get
        {
            lock (_supervisors)
                return _supervisors.Select(s => new LoadedContainerPlugin(
                    s.Descriptor.Name,
                    s.Descriptor.Image,
                    s.Descriptor.HandlerTypeName,
                    s.Descriptor.TargetApiVersion,
                    s.Status)
                {
                    Topology = s.Descriptor.Topology switch
                    {
                        ContainerTopology.Kubernetes => "kubernetes",
                        ContainerTopology.Sidecar    => "sidecar",
                        _                            => "standalone",
                    },
                    KubernetesDeploymentName = s.Descriptor.KubernetesConfig?.DeploymentName,
                    KubernetesNamespace      = s.Descriptor.KubernetesConfig?.Namespace,
                }).ToList();
        }
    }

    internal bool TryGetSupervisor(string pluginName, out IContainerSupervisor? supervisor)
    {
        lock (_supervisors)
        {
            supervisor = _supervisors.FirstOrDefault(s =>
                string.Equals(s.Descriptor.Name, pluginName, StringComparison.OrdinalIgnoreCase));
            return supervisor is not null;
        }
    }

    public ContainerPluginHostService(
        ContainerPluginLoaderOptions options,
        IPluginHandlerRegistry registry,
        ILoggerFactory loggerFactory)
    {
        _options = options;
        _registry = registry;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ContainerPluginHostService>();
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var descriptors = await ScanDescriptorsAsync(cancellationToken).ConfigureAwait(false);
        if (descriptors.Count == 0)
        {
            _logger.LogDebug("No container plugins found in '{Directory}'", _options.PluginsDirectory);
            return;
        }

        _logger.LogInformation("Starting {Count} container plugin(s)", descriptors.Count);
        await Parallel.ForEachAsync(
            descriptors,
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelism, CancellationToken = cancellationToken },
            StartOneAsync).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var s in _supervisors)
        {
            try { await s.StopAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error stopping container plugin '{Name}'", s.Descriptor.Name);
            }
        }
    }

    private async ValueTask StartOneAsync(ContainerPluginDescriptor descriptor, CancellationToken ct)
    {
        IContainerSupervisor supervisor;
        if (descriptor.KubernetesConfig is not null)
        {
            var k8sCfg = KubernetesClientConfiguration.BuildDefaultConfig();
            var k8sClient = new Kubernetes(k8sCfg);
            supervisor = new KubernetesContainerSupervisor(
                descriptor, k8sClient, _loggerFactory.CreateLogger<KubernetesContainerSupervisor>());
        }
        else
        {
            var docker = new DockerClientConfiguration().CreateClient();
            supervisor = new DockerContainerSupervisor(
                descriptor, docker, _loggerFactory.CreateLogger<DockerContainerSupervisor>());
        }

        try
        {
            await supervisor.StartAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to start container plugin '{Name}' from image '{Image}'",
                descriptor.Name, descriptor.Image);
            await supervisor.DisposeAsync().ConfigureAwait(false);
            return;
        }

        using var metaClient = new HttpClient
        {
            BaseAddress = new Uri(descriptor.InvokeBaseUrl),
            Timeout = TimeSpan.FromSeconds(10),
        };

        // K8s topology: deployment may not be ready at runtime startup — retry until reachable.
        int metaRetrySeconds = descriptor.KubernetesConfig is not null ? descriptor.StartupTimeoutSeconds : 0;
        var metaDeadline = DateTimeOffset.UtcNow.AddSeconds(metaRetrySeconds);

        PluginMetadataResponse? meta = null;
        while (true)
        {
            try
            {
                var metaResp = await metaClient.GetAsync("/v1/metadata", ct).ConfigureAwait(false);
                metaResp.EnsureSuccessStatusCode();
                meta = await metaResp.Content.ReadFromJsonAsync<PluginMetadataResponse>(
                    ContainerJsonOptions.Default, ct).ConfigureAwait(false);
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && DateTimeOffset.UtcNow < metaDeadline)
            {
                await Task.Delay(2000, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[{Urn}] Container plugin '{Name}': failed to fetch /v1/metadata",
                    ContainerPluginUrns.AbiFailed, descriptor.Name);
                await supervisor.DisposeAsync().ConfigureAwait(false);
                return;
            }
        }

        if (meta is null || string.IsNullOrEmpty(meta.HandlerTypeName))
        {
            _logger.LogError(
                "[{Urn}] Container plugin '{Name}': /v1/metadata returned empty handlerTypeName",
                ContainerPluginUrns.AbiFailed, descriptor.Name);
            await supervisor.DisposeAsync().ConfigureAwait(false);
            return;
        }

        if (!IsVersionInRange(meta.TargetApiVersion, _options.SupportedApiVersionMin, _options.SupportedApiVersionMax))
        {
            _logger.LogError(
                "[{Urn}] Container plugin '{Name}': targetApiVersion '{Version}' not in supported range [{Min}, {Max}]",
                ContainerPluginUrns.AbiFailed, descriptor.Name, meta.TargetApiVersion,
                _options.SupportedApiVersionMin, _options.SupportedApiVersionMax);
            await supervisor.DisposeAsync().ConfigureAwait(false);
            return;
        }

        descriptor.HandlerTypeName = meta.HandlerTypeName;
        descriptor.TargetApiVersion = meta.TargetApiVersion;

        var factory = new ContainerAgentShimFactory(supervisor, descriptor, _options, _loggerFactory);

        try
        {
            _registry.Register(factory, descriptor.Name);
            lock (_supervisors) _supervisors.Add(supervisor);
            _logger.LogInformation(
                "Container plugin '{Name}' registered as handler '{TypeName}' (apiVersion={Version})",
                descriptor.Name, descriptor.HandlerTypeName, descriptor.TargetApiVersion);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to register container plugin '{Name}' handler '{TypeName}'",
                descriptor.Name, descriptor.HandlerTypeName);
            await supervisor.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<List<ContainerPluginDescriptor>> ScanDescriptorsAsync(CancellationToken ct)
    {
        var dir = _options.PluginsDirectory;
        if (!Directory.Exists(dir))
        {
            _logger.LogDebug("Container plugins directory '{Directory}' does not exist", dir);
            return [];
        }

        var deserializer = new ContainerPluginYamlDeserializer();
        var result = new List<ContainerPluginDescriptor>();

        foreach (var pluginDir in Directory.GetDirectories(dir))
        {
            ct.ThrowIfCancellationRequested();
            var yamlPath = Path.Combine(pluginDir, "plugin.yaml");
            if (!File.Exists(yamlPath)) continue;

            string yaml;
            try { yaml = await File.ReadAllTextAsync(yamlPath, ct).ConfigureAwait(false); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read '{YamlPath}'", yamlPath);
                continue;
            }

            ContainerPluginYamlDocument? doc;
            try { doc = deserializer.Deserialize(yaml); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not parse YAML in '{YamlPath}'", yamlPath);
                continue;
            }

            if (doc?.Spec?.Runtime is not "container") continue;

            var spec = doc.Spec;
            if (string.IsNullOrEmpty(spec.Image))
            {
                _logger.LogWarning("Container plugin in '{Dir}' has no image specified — skipping", pluginDir);
                continue;
            }

            ContainerTopology topology;
            KubernetesPluginConfig? k8sConfig = null;
            string invokeBaseUrl;

            if (spec.Kubernetes is { } k8sSpec)
            {
                if (string.IsNullOrEmpty(k8sSpec.ServiceUrl))
                {
                    _logger.LogWarning(
                        "Container plugin in '{Dir}' has kubernetes section but no serviceUrl — skipping",
                        pluginDir);
                    continue;
                }
                topology = ContainerTopology.Kubernetes;
                k8sConfig = new KubernetesPluginConfig(k8sSpec.ServiceUrl, k8sSpec.DeploymentName, k8sSpec.Namespace);
                invokeBaseUrl = k8sSpec.ServiceUrl;
            }
            else
            {
                topology = spec.Topology.Equals("sidecar", StringComparison.OrdinalIgnoreCase)
                    ? ContainerTopology.Sidecar : ContainerTopology.Standalone;
                invokeBaseUrl = $"http://localhost:{spec.Port}";

                if (topology == ContainerTopology.Standalone && string.IsNullOrEmpty(spec.Durability))
                    _logger.LogWarning(
                        "Container plugin in '{Dir}' uses standalone topology without a durability setting; " +
                        "plugin state may be lost on silo restart",
                        pluginDir);
            }

            ContainerRetryPolicy? retryPolicy = null;
            if (spec.RetryPolicy is { } rp)
                retryPolicy = new ContainerRetryPolicy(rp.MaxAttempts, rp.BackoffSeconds, rp.RetryOn);

            var descriptor = new ContainerPluginDescriptor
            {
                Name = doc.Metadata?.Name is { Length: > 0 } n ? n : Path.GetFileName(pluginDir),
                Image = spec.Image,
                Port = spec.Port,
                Topology = topology,
                StartupTimeoutSeconds = spec.StartupTimeoutSeconds,
                InvokeTimeoutSeconds = spec.InvokeTimeoutSeconds,
                RetryPolicy = retryPolicy,
                SecretRefs = new Dictionary<string, string>(spec.Secrets),
                InvokeBaseUrl = invokeBaseUrl,
                KubernetesConfig = k8sConfig,
            };
            result.Add(descriptor);
        }

        return result;
    }

    private static bool IsVersionInRange(string version, string min, string max)
    {
        if (string.IsNullOrEmpty(version)) return false;
        return string.Compare(version, min, StringComparison.Ordinal) >= 0
            && string.Compare(version, max, StringComparison.Ordinal) <= 0;
    }
}
