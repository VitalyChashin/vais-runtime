// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Npgsql;

namespace Vais.Agents.Runtime.Host;

internal sealed class PostgresSelfCheckProbe : ISelfCheckProbe
{
    private readonly string _connectionString;
    private readonly string _sql;

    public string ServiceName { get; }
    public bool IsRequired { get; }

    public PostgresSelfCheckProbe(string serviceName, string connectionString, string sql, bool isRequired = false)
    {
        ServiceName = serviceName;
        _connectionString = connectionString;
        _sql = sql;
        IsRequired = isRequired;
    }

    public async Task<SelfCheckResult> ProbeAsync(CancellationToken ct)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(_sql, conn);
            await cmd.ExecuteScalarAsync(ct);
            return new SelfCheckResult(ServiceName, IsRequired, true);
        }
        catch (Exception ex)
        {
            return new SelfCheckResult(ServiceName, IsRequired, false, ex.Message);
        }
    }
}
