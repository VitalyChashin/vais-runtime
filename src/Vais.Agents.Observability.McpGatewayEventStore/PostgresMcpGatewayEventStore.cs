// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging;
using Npgsql;

namespace Vais.Agents.Observability.McpGatewayEventStore;

internal sealed class PostgresMcpGatewayEventStore : IMcpGatewayEventStore
{
    private readonly string _connectionString;
    private readonly ILogger<PostgresMcpGatewayEventStore> _logger;

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS vais_mcp_gateway_events (
            event_id        TEXT PRIMARY KEY,
            gateway_id      TEXT NOT NULL,
            tool_name       TEXT NOT NULL,
            event_kind      TEXT NOT NULL,
            duration_ms     BIGINT,
            cache_hit       BOOLEAN NOT NULL DEFAULT false,
            blocked_reason  TEXT,
            error_type      TEXT,
            at              TIMESTAMPTZ NOT NULL,
            correlation_id  TEXT,
            run_id          TEXT,
            created_at      TIMESTAMPTZ NOT NULL DEFAULT now());
        CREATE INDEX IF NOT EXISTS idx_vais_mcp_gw_events_gateway_id  ON vais_mcp_gateway_events(gateway_id, at DESC);
        CREATE INDEX IF NOT EXISTS idx_vais_mcp_gw_events_tool_name   ON vais_mcp_gateway_events(tool_name);
        CREATE INDEX IF NOT EXISTS idx_vais_mcp_gw_events_run_id      ON vais_mcp_gateway_events(run_id) WHERE run_id IS NOT NULL;
        CREATE INDEX IF NOT EXISTS idx_vais_mcp_gw_events_created_at  ON vais_mcp_gateway_events(created_at);
        ALTER TABLE vais_mcp_gateway_events ADD COLUMN IF NOT EXISTS input_json  TEXT;
        ALTER TABLE vais_mcp_gateway_events ADD COLUMN IF NOT EXISTS output_json TEXT;
        """;

    public PostgresMcpGatewayEventStore(string connectionString, ILogger<PostgresMcpGatewayEventStore> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = Schema;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _logger.LogDebug("McpGatewayEventStore schema applied.");
    }

    public async Task RecordAsync(McpGatewayEvent evt, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO vais_mcp_gateway_events
                (event_id, gateway_id, tool_name, event_kind, duration_ms, cache_hit,
                 blocked_reason, error_type, at, correlation_id, run_id,
                 input_json, output_json)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13)
            ON CONFLICT (event_id) DO NOTHING
            """;
        cmd.Parameters.AddWithValue(evt.EventId);
        cmd.Parameters.AddWithValue(evt.GatewayId);
        cmd.Parameters.AddWithValue(evt.ToolName);
        cmd.Parameters.AddWithValue(evt.EventKind);
        cmd.Parameters.AddWithValue((object?)evt.DurationMs ?? DBNull.Value);
        cmd.Parameters.AddWithValue(evt.CacheHit);
        cmd.Parameters.AddWithValue((object?)evt.BlockedReason ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)evt.ErrorType ?? DBNull.Value);
        cmd.Parameters.AddWithValue(evt.At);
        cmd.Parameters.AddWithValue((object?)evt.CorrelationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)evt.RunId ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)evt.InputJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)evt.OutputJson ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<McpGatewayEvent>> ListAsync(string gatewayId,
        DateTimeOffset? since = null, DateTimeOffset? until = null,
        string? toolName = null, string? kind = null, int limit = 50,
        CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();

        var conditions = new List<string> { "gateway_id = $1" };
        var paramIdx = 2;
        if (since.HasValue) conditions.Add($"at >= ${paramIdx++}");
        if (until.HasValue) conditions.Add($"at <= ${paramIdx++}");
        if (toolName is not null) conditions.Add($"tool_name = ${paramIdx++}");
        if (kind is not null) conditions.Add($"event_kind = ${paramIdx++}");

        cmd.CommandText = $"""
            SELECT event_id, gateway_id, tool_name, event_kind, duration_ms, cache_hit,
                   blocked_reason, error_type, at, correlation_id, run_id,
                   input_json, output_json
            FROM vais_mcp_gateway_events
            WHERE {string.Join(" AND ", conditions)}
            ORDER BY at DESC
            LIMIT ${paramIdx}
            """;

        cmd.Parameters.AddWithValue(gatewayId);
        if (since.HasValue) cmd.Parameters.AddWithValue(since.Value);
        if (until.HasValue) cmd.Parameters.AddWithValue(until.Value);
        if (toolName is not null) cmd.Parameters.AddWithValue(toolName);
        if (kind is not null) cmd.Parameters.AddWithValue(kind);
        cmd.Parameters.AddWithValue(limit);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<McpGatewayEvent>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            result.Add(ReadEvent(reader));
        return result;
    }

    public async Task<IReadOnlyList<McpGatewayEvent>> ListByRunAsync(string runId, int limit = 200, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT event_id, gateway_id, tool_name, event_kind, duration_ms, cache_hit,
                   blocked_reason, error_type, at, correlation_id, run_id,
                   input_json, output_json
            FROM vais_mcp_gateway_events
            WHERE run_id = $1
            ORDER BY at DESC
            LIMIT $2
            """;
        cmd.Parameters.AddWithValue(runId);
        cmd.Parameters.AddWithValue(limit);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<McpGatewayEvent>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            result.Add(ReadEvent(reader));
        return result;
    }

    public async Task DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM vais_mcp_gateway_events WHERE created_at < $1";
        cmd.Parameters.AddWithValue(cutoff);
        var deleted = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (deleted > 0)
            _logger.LogInformation("McpGatewayEventStore pruned {Count} events older than {Cutoff:u}.", deleted, cutoff);
    }

    public async Task<IReadOnlyList<McpGatewayEvent>> QueryFailedAcrossGatewaysAsync(
        string? toolName = null,
        DateTimeOffset? since = null,
        int limit = 50,
        CancellationToken ct = default)
    {
        var conditions = new List<string> { "error_type IS NOT NULL" };
        var parameters = new List<object>();
        if (!string.IsNullOrEmpty(toolName))
        {
            conditions.Add($"tool_name = ${parameters.Count + 1}");
            parameters.Add(toolName);
        }
        if (since.HasValue)
        {
            conditions.Add($"at >= ${parameters.Count + 1}");
            parameters.Add(since.Value);
        }

        var limitParam = $"${parameters.Count + 1}";
        parameters.Add(limit);

        var sql = $"""
            SELECT event_id, gateway_id, tool_name, event_kind, duration_ms, cache_hit,
                   blocked_reason, error_type, at, correlation_id, run_id,
                   input_json, output_json
            FROM vais_mcp_gateway_events
            WHERE {string.Join(" AND ", conditions)}
            ORDER BY at DESC
            LIMIT {limitParam}
            """;

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var p in parameters)
            cmd.Parameters.AddWithValue(p);

        var result = new List<McpGatewayEvent>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            result.Add(ReadEvent(reader));
        return result;
    }

    private async ValueTask<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return conn;
    }

    private static McpGatewayEvent ReadEvent(NpgsqlDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
            r.IsDBNull(4) ? null : r.GetInt64(4),
            r.GetBoolean(5),
            r.IsDBNull(6) ? null : r.GetString(6),
            r.IsDBNull(7) ? null : r.GetString(7),
            r.GetFieldValue<DateTimeOffset>(8),
            r.IsDBNull(9) ? null : r.GetString(9),
            r.IsDBNull(10) ? null : r.GetString(10),
            r.IsDBNull(11) ? null : r.GetString(11),
            r.IsDBNull(12) ? null : r.GetString(12));
}
