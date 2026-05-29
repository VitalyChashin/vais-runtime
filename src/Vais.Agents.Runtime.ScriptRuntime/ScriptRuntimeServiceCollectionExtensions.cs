// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Vais.Agents.ScriptRuntime;

/// <summary>DI registration for code-mode (the ScriptRuntime primitive's runtime side).</summary>
public static class ScriptRuntimeServiceCollectionExtensions
{
    /// <summary>
    /// Register the code-mode services: the raw JS-API generator, the typed sidecar client, and the
    /// <see cref="ICodeModeToolFactory"/> the manifest translator resolves to build <c>run_code</c>.
    /// When this is not called, a manifest with <c>spec.codeMode.enabled: true</c> fails to activate
    /// with a clear error.
    /// </summary>
    public static IServiceCollection AddScriptRuntime(this IServiceCollection services, Action<ScriptRuntimeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new ScriptRuntimeOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);

        services.TryAddSingleton<IToolApiGenerator, RawMcpClientGenerator>();
        services.TryAddSingleton<ICodeModeToolFactory, CodeModeToolFactory>();

        services.AddHttpClient<IScriptRuntimeClient, HttpScriptRuntimeClient>((sp, client) =>
        {
            var o = sp.GetRequiredService<ScriptRuntimeOptions>();
            var baseUrl = o.SidecarBaseUrl.EndsWith('/') ? o.SidecarBaseUrl : o.SidecarBaseUrl + "/";
            client.BaseAddress = new Uri(baseUrl);
        });

        return services;
    }
}
