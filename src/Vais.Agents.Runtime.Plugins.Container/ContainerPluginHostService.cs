// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net.Http.Json;
using Docker.DotNet;
using k8s;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vais.Agents.Core;
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
    private readonly IContainerPluginRegistry? _containerPluginRegistry;
    private readonly ICallTokenService? _callTokenService;
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
        ILoggerFactory loggerFactory,
        IContainerPluginRegistry? containerPluginRegistry = null,
        ICallTokenService? callTokenService = null)
    {
        _options = options;
        _registry = registry;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ContainerPluginHostService>();
        _containerPluginRegistry = containerPluginRegistry;
        _callTokenService = callTokenService;
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

    /// <inheritdoc />
    public async ValueTask RegisterAsync(ContainerPluginManifest manifest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var descriptor = ManifestToDescriptor(manifest, _options.PluginNetwork);
        await StartCoreAsync(descriptor, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask UnregisterAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        IContainerSupervisor? supervisor;
        lock (_supervisors)
        {
            supervisor = _supervisors.FirstOrDefault(s =>
                string.Equals(s.Descriptor.Name, id, StringComparison.OrdinalIgnoreCase));
            if (supervisor is not null)
                _supervisors.Remove(supervisor);
        }
        if (supervisor is null) return;
        try
        {
            await supervisor.StopAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            await supervisor.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask StartOneAsync(ContainerPluginDescriptor descriptor, CancellationToken ct)
    {
        try
        {
            await StartCoreAsync(descriptor, ct).ConfigureAwait(false);
            if (_containerPluginRegistry is not null)
            {
                try
                {
                    await _containerPluginRegistry.RegisterAsync(DescriptorToManifest(descriptor), ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to promote filesystem container plugin '{Name}' into registry", descriptor.Name);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start container plugin '{Name}'", descriptor.Name);
        }
    }

    private async ValueTask StartCoreAsync(ContainerPluginDescriptor descriptor, CancellationToken ct)
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
                descriptor, docker, _loggerFactory.CreateLogger<DockerContainerSupervisor>(),
                _callTokenService, _options.OtlpEndpointUrl, _options.LogEndpointUrl);
        }

        try
        {
            await supervisor.StartAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await supervisor.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Container plugin '{descriptor.Name}' failed to start: {ex.Message}", ex);
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
                await supervisor.DisposeAsync().ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"[{ContainerPluginUrns.AbiFailed}] Container plugin '{descriptor.Name}': failed to fetch /v1/metadata", ex);
            }
        }

        if (meta is null || string.IsNullOrEmpty(meta.HandlerTypeName))
        {
            await supervisor.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException(
                $"[{ContainerPluginUrns.AbiFailed}] Container plugin '{descriptor.Name}': /v1/metadata returned empty handlerTypeName");
        }

        if (!IsVersionInRange(meta.TargetApiVersion, _options.SupportedApiVersionMin, _options.SupportedApiVersionMax))
        {
            await supervisor.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException(
                $"[{ContainerPluginUrns.AbiFailed}] Container plugin '{descriptor.Name}': targetApiVersion '{meta.TargetApiVersion}' not in supported range [{_options.SupportedApiVersionMin}, {_options.SupportedApiVersionMax}]");
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
            await supervisor.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Failed to register container plugin '{descriptor.Name}' handler '{descriptor.HandlerTypeName}'", ex);
        }
    }

    private static ContainerPluginManifest DescriptorToManifest(ContainerPluginDescriptor descriptor)
    {
        ContainerPluginKubernetesConfig? k8s = null;
        if (descriptor.KubernetesConfig is { } k8sCfg)
            k8s = new ContainerPluginKubernetesConfig
            {
                ServiceUrl = k8sCfg.ServiceUrl,
                DeploymentName = k8sCfg.DeploymentName,
                Namespace = k8sCfg.Namespace,
            };

        return new ContainerPluginManifest(descriptor.Name, Version: "1.0")
        {
            Spec = new ContainerPluginSpec
            {
                Image = descriptor.Image,
                Port = descriptor.Port,
                Topology = descriptor.Topology switch
                {
                    ContainerTopology.Kubernetes => "kubernetes",
                    ContainerTopology.Sidecar    => "sidecar",
                    _                            => "standalone",
                },
                StartupTimeoutSeconds = descriptor.StartupTimeoutSeconds,
                InvokeTimeoutSeconds = descriptor.InvokeTimeoutSeconds,
                SessionTtlSeconds = descriptor.SessionTtlSeconds,
                InvokeIdleTimeoutSeconds = descriptor.InvokeIdleTimeoutSeconds,
                RetryPolicy = descriptor.RetryPolicy is { } rp
                    ? new ContainerPluginRetryPolicy(rp.MaxAttempts, rp.BackoffSeconds, rp.RetryOn)
                    : null,
                Kubernetes = k8s,
                Secrets = descriptor.SecretRefs.Count > 0
                    ? new Dictionary<string, string>(descriptor.SecretRefs)
                    : null,
            }
        };
    }

    private static ContainerPluginDescriptor ManifestToDescriptor(ContainerPluginManifest manifest, string? pluginNetwork = null)
    {
        var spec = manifest.Spec;
        ContainerTopology topology;
        KubernetesPluginConfig? k8sConfig = null;
        string invokeBaseUrl;
        string? dockerNetwork = null;

        if (spec.Kubernetes is { } k8s)
        {
            topology = ContainerTopology.Kubernetes;
            k8sConfig = new KubernetesPluginConfig(k8s.ServiceUrl, k8s.DeploymentName, k8s.Namespace);
            invokeBaseUrl = k8s.ServiceUrl;
        }
        else
        {
            topology = spec.Topology.Equals("sidecar", StringComparison.OrdinalIgnoreCase)
                ? ContainerTopology.Sidecar : ContainerTopology.Standalone;
            dockerNetwork = pluginNetwork;
            invokeBaseUrl = DockerNaming.InvokeUrl(manifest.Id, spec.Port, pluginNetwork);
        }

        ContainerRetryPolicy? retryPolicy = null;
        if (spec.RetryPolicy is { } rp)
            retryPolicy = new ContainerRetryPolicy(rp.MaxAttempts, rp.BackoffSeconds, rp.RetryOn);

        return new ContainerPluginDescriptor
        {
            Name = manifest.Id,
            Image = spec.Image,
            Port = spec.Port,
            Topology = topology,
            StartupTimeoutSeconds = spec.StartupTimeoutSeconds,
            InvokeTimeoutSeconds = spec.InvokeTimeoutSeconds,
            SessionTtlSeconds = spec.SessionTtlSeconds,
            InvokeIdleTimeoutSeconds = spec.InvokeIdleTimeoutSeconds,
            RetryPolicy = retryPolicy,
            SecretRefs = spec.Secrets is not null
                ? new Dictionary<string, string>(spec.Secrets)
                : new Dictionary<string, string>(),
            InvokeBaseUrl = invokeBaseUrl,
            KubernetesConfig = k8sConfig,
            DockerPluginNetwork = dockerNetwork,
        };
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

            var pluginName = doc.Metadata?.Name is { Length: > 0 } n ? n : Path.GetFileName(pluginDir);

            ContainerTopology topology;
            KubernetesPluginConfig? k8sConfig = null;
            string invokeBaseUrl;
            string? dockerNetwork = null;

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
                dockerNetwork = _options.PluginNetwork;
                invokeBaseUrl = DockerNaming.InvokeUrl(pluginName, spec.Port, _options.PluginNetwork);

                if (topology == ContainerTopology.Standalone && string.IsNullOrEmpty(spec.Durability))
                    _logger.LogWarning(
                        "Container plugin in '{Dir}' uses standalone topology without a durability setting; " +
                        "plugin state may be lost on silo restart",
                        pluginDir);
            }

            ContainerRetryPolicy? retryPolicy = null;
            if (spec.RetryPolicy is { } rp)
                retryPolicy = new ContainerRetryPolicy(rp.MaxAttempts, rp.BackoffSeconds, rp.RetryOn);

            var bounds = _options.ResourceBounds;
            var descriptor = new ContainerPluginDescriptor
            {
                Name = pluginName,
                Image = spec.Image,
                Port = spec.Port,
                Topology = topology,
                StartupTimeoutSeconds = spec.StartupTimeoutSeconds,
                InvokeTimeoutSeconds = spec.InvokeTimeoutSeconds,
                SessionTtlSeconds = spec.SessionTtlSeconds,
                InvokeIdleTimeoutSeconds = spec.InvokeIdleTimeoutSeconds,
                RetryPolicy = retryPolicy,
                SecretRefs = new Dictionary<string, string>(spec.Secrets),
                InvokeBaseUrl = invokeBaseUrl,
                KubernetesConfig = k8sConfig,
                DockerPluginNetwork = dockerNetwork,
                MemoryBytes = ContainerPluginResourceParser.Clamp(
                    ContainerPluginResourceParser.ParseMemoryBytes(spec.Resources?.Memory), bounds.MaxMemoryBytes),
                NanoCpus = ContainerPluginResourceParser.Clamp(
                    ContainerPluginResourceParser.ParseNanoCpus(spec.Resources?.Cpu), bounds.MaxNanoCpus),
                PidsLimit = ContainerPluginResourceParser.Clamp(
                    spec.Resources?.PidsLimit, bounds.MaxPidsLimit),
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
