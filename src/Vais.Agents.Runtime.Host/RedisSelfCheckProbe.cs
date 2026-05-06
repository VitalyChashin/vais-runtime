// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using StackExchange.Redis;

namespace Vais.Agents.Runtime.Host;

internal sealed class RedisSelfCheckProbe : ISelfCheckProbe
{
    private readonly string _connectionString;

    public string ServiceName => "redis";
    public bool IsRequired => false;

    public RedisSelfCheckProbe(string connectionString) => _connectionString = connectionString;

    public async Task<SelfCheckResult> ProbeAsync(CancellationToken ct)
    {
        try
        {
            var config = ConfigurationOptions.Parse(_connectionString);
            config.ConnectTimeout = 2000;
            config.AbortOnConnectFail = true;
            using var conn = await ConnectionMultiplexer.ConnectAsync(config);
            await conn.GetDatabase().PingAsync();
            return new SelfCheckResult(ServiceName, IsRequired, true);
        }
        catch (Exception ex)
        {
            return new SelfCheckResult(ServiceName, IsRequired, false, ex.Message);
        }
    }
}
