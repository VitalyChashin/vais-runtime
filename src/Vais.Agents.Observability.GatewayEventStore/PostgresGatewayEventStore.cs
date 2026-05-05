// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging;
using Npgsql;

namespace Vais.Agents.Observability.GatewayEventStore;

internal sealed class PostgresGatewayEventStore : IGatewayEventStore
{
    private readonly string _connectionString;
    private readonly ILogger<PostgresGatewayEventStore> _logger;

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS vais_gateway_events (
            event_id       TEXT PRIMARY KEY,
            gateway_id     TEXT NOT NULL,
            event_kind     TEXT NOT NULL,
            model_id       TEXT,
            input_tokens   INT  NOT NULL DEFAULT 0,
            output_tokens  INT  NOT NULL DEFAULT 0,
            duration_ms    BIGINT,
            cache_hit      BOOLEAN,
            error_type     TEXT,
            at             TIMESTAMPTZ NOT NULL,
            correlation_id TEXT,
            run_id         TEXT,
            created_at     TIMESTAMPTZ NOT NULL DEFAULT now());
        CREATE INDEX IF NOT EXISTS idx_vais_gateway_events_gateway_id ON vais_gateway_events(gateway_id, at DESC);
        CREATE INDEX IF NOT EXISTS idx_vais_gateway_events_run_id     ON vais_gateway_events(run_id) WHERE run_id IS NOT NULL;
        CREATE INDEX IF NOT EXISTS idx_vais_gateway_events_created_at ON vais_gateway_events(created_at);
        """;

    public PostgresGatewayEventStore(string connectionString, ILogger<PostgresGatewayEventStore> logger)
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
        _logger.LogDebug("GatewayEventStore schema applied.");
    }

    public async Task RecordAsync(GatewayEvent evt, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO vais_gateway_events
                (event_id, gateway_id, event_kind, model_id, input_tokens, output_tokens,
                 duration_ms, cache_hit, error_type, at, correlation_id, run_id)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12)
            ON CONFLICT (event_id) DO NOTHING
            """;
        cmd.Parameters.AddWithValue(evt.EventId);
        cmd.Parameters.AddWithValue(evt.GatewayId);
        cmd.Parameters.AddWithValue(evt.EventKind);
        cmd.Parameters.AddWithValue((object?)evt.ModelId ?? DBNull.Value);
        cmd.Parameters.AddWithValue(evt.InputTokens);
        cmd.Parameters.AddWithValue(evt.OutputTokens);
        cmd.Parameters.AddWithValue((object?)evt.DurationMs ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)evt.CacheHit ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)evt.ErrorType ?? DBNull.Value);
        cmd.Parameters.AddWithValue(evt.At);
        cmd.Parameters.AddWithValue((object?)evt.CorrelationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)evt.RunId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GatewayEvent>> ListAsync(string gatewayId,
        DateTimeOffset? since = null, DateTimeOffset? until = null,
        string? kind = null, int limit = 50, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();

        var conditions = new List<string> { "gateway_id = $1" };
        var paramIdx = 2;
        if (since.HasValue) conditions.Add($"at >= ${paramIdx++}");
        if (until.HasValue) conditions.Add($"at <= ${paramIdx++}");
        if (kind is not null) conditions.Add($"event_kind = ${paramIdx++}");

        cmd.CommandText = $"""
            SELECT event_id, gateway_id, event_kind, model_id, input_tokens, output_tokens,
                   duration_ms, cache_hit, error_type, at, correlation_id, run_id
            FROM vais_gateway_events
            WHERE {string.Join(" AND ", conditions)}
            ORDER BY at DESC
            LIMIT ${paramIdx}
            """;

        cmd.Parameters.AddWithValue(gatewayId);
        if (since.HasValue) cmd.Parameters.AddWithValue(since.Value);
        if (until.HasValue) cmd.Parameters.AddWithValue(until.Value);
        if (kind is not null) cmd.Parameters.AddWithValue(kind);
        cmd.Parameters.AddWithValue(limit);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<GatewayEvent>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            result.Add(ReadEvent(reader));
        return result;
    }

    public async Task DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM vais_gateway_events WHERE created_at < $1";
        cmd.Parameters.AddWithValue(cutoff);
        var deleted = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (deleted > 0)
            _logger.LogInformation("GatewayEventStore pruned {Count} events older than {Cutoff:u}.", deleted, cutoff);
    }

    private async ValueTask<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return conn;
    }

    private static GatewayEvent ReadEvent(NpgsqlDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2),
            r.IsDBNull(3) ? null : r.GetString(3),
            r.GetInt32(4),
            r.GetInt32(5),
            r.IsDBNull(6) ? null : r.GetInt64(6),
            r.IsDBNull(7) ? null : r.GetBoolean(7),
            r.IsDBNull(8) ? null : r.GetString(8),
            r.GetFieldValue<DateTimeOffset>(9),
            r.IsDBNull(10) ? null : r.GetString(10),
            r.IsDBNull(11) ? null : r.GetString(11));
}
