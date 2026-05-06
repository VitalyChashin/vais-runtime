// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Runtime.Host;

internal sealed class HttpSelfCheckProbe : ISelfCheckProbe
{
    private readonly string _url;

    public string ServiceName { get; }
    public bool IsRequired => false;

    public HttpSelfCheckProbe(string serviceName, string url)
    {
        ServiceName = serviceName;
        _url = url;
    }

    public async Task<SelfCheckResult> ProbeAsync(CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            // Any HTTP response (including 4xx/5xx) confirms the service is reachable.
            await http.GetAsync(_url, HttpCompletionOption.ResponseHeadersRead, ct);
            return new SelfCheckResult(ServiceName, IsRequired, true);
        }
        catch (Exception ex)
        {
            return new SelfCheckResult(ServiceName, IsRequired, false, ex.Message);
        }
    }
}
