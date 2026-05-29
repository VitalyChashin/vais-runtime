// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Net.Http.Json;
using System.Text.Json;

namespace Vais.Agents.ScriptRuntime;

/// <summary>
/// <see cref="IScriptRuntimeClient"/> over HTTP. Registered as a typed client whose
/// <see cref="HttpClient.BaseAddress"/> is the supervised sidecar; posts the run request to
/// <c>v1/script/run</c> and deserializes the sidecar's <see cref="ScriptRunResponse"/>.
/// </summary>
internal sealed class HttpScriptRuntimeClient(HttpClient http) : IScriptRuntimeClient
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<ScriptRunResponse> RunAsync(ScriptRunRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var resp = await http.PostAsJsonAsync("v1/script/run", request, JsonOpts, cancellationToken).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        return await resp.Content.ReadFromJsonAsync<ScriptRunResponse>(JsonOpts, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("ScriptRuntime sidecar returned an empty response body.");
    }
}
