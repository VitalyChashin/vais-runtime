// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Npgsql;

namespace Vais.Agents.Observability.RunHealthStore;

/// <summary>
/// PostgreSQL-backed <see cref="IRunHealthStore"/>. Mirrors the schema/initialise pattern of
/// <c>PostgresAgentRunStore</c>: one table, idempotent <c>CREATE TABLE IF NOT EXISTS</c> on
/// <see cref="InitializeAsync"/>, parameterised inserts/queries throughout. Writes are
/// idempotent under duplicate delivery via the composite PK + <c>ON CONFLICT DO NOTHING</c>.
/// </summary>
public sealed class PostgresRunHealthStore : IRunHealthStore
{
    private readonly string _connectionString;

    /// <summary>Creates a store over the given PostgreSQL connection string.</summary>
    public PostgresRunHealthStore(string connectionString)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return conn;
    }

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS vais_run_health_signals (
                run_id          TEXT NOT NULL,
                correlation_id  TEXT,
                signal_kind     TEXT NOT NULL,
                level           SMALLINT NOT NULL,
                source          TEXT NOT NULL,
                error_type      TEXT,
                is_transient    BOOLEAN NOT NULL,
                at              TIMESTAMPTZ NOT NULL,
                created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
                PRIMARY KEY (run_id, at, signal_kind, source)
            );
            CREATE INDEX IF NOT EXISTS idx_vais_run_health_run_id ON vais_run_health_signals(run_id);
            CREATE INDEX IF NOT EXISTS idx_vais_run_health_correlation_id ON vais_run_health_signals(correlation_id) WHERE correlation_id IS NOT NULL;
            ALTER TABLE vais_run_health_signals ADD COLUMN IF NOT EXISTS concept_name TEXT;
            ALTER TABLE vais_run_health_signals ADD COLUMN IF NOT EXISTS attribution_path TEXT;
            """;

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RecordSignalAsync(RunHealthSignalRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        const string sql = """
            INSERT INTO vais_run_health_signals
                (run_id, correlation_id, signal_kind, level, source, error_type, is_transient, at, concept_name, attribution_path)
            VALUES ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)
            ON CONFLICT (run_id, at, signal_kind, source) DO NOTHING
            """;

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue(record.RunId);
        cmd.Parameters.AddWithValue((object?)record.CorrelationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue(record.Kind.ToString());
        cmd.Parameters.AddWithValue((short)record.Level);
        cmd.Parameters.AddWithValue(record.Source);
        cmd.Parameters.AddWithValue((object?)record.ErrorType ?? DBNull.Value);
        cmd.Parameters.AddWithValue(record.IsTransient);
        cmd.Parameters.AddWithValue(record.At);
        cmd.Parameters.AddWithValue((object?)record.ConceptName ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)record.AttributionPath ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RunHealthSignal>> ListByRunTreeAsync(string rootRunId, CancellationToken ct = default)
    {
        // The run tree: the exact root plus all agent-as-tool descendants, whose run ids are
        // "{parentRun}__{name}__{hash}" — so every descendant is prefixed by "{root}__".
        const string sql = """
            SELECT source, signal_kind, level, error_type, is_transient, at, concept_name, attribution_path
            FROM vais_run_health_signals
            WHERE run_id = $1 OR starts_with(run_id, $2)
            ORDER BY at
            """;

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue(rootRunId);
        cmd.Parameters.AddWithValue(rootRunId + "__");

        var result = new List<RunHealthSignal>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var source = reader.GetString(0);
            var kind = Enum.TryParse<RunHealthSignalKind>(reader.GetString(1), out var k) ? k : RunHealthSignalKind.TurnFailed;
            var level = (FailureLevel)reader.GetInt16(2);
            var errorType = reader.IsDBNull(3) ? null : reader.GetString(3);
            var isTransient = reader.GetBoolean(4);
            var at = reader.GetFieldValue<DateTimeOffset>(5);
            var conceptName = reader.IsDBNull(6) ? null : reader.GetString(6);
            var attributionPath = reader.IsDBNull(7) ? null : reader.GetString(7);
            result.Add(new RunHealthSignal(source, kind, level, errorType, isTransient, at, conceptName, attributionPath));
        }
        return result;
    }

    /// <inheritdoc />
    public async Task DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM vais_run_health_signals WHERE created_at < $1";
        cmd.Parameters.AddWithValue(cutoff);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RunHealthSignalRecord>> QuerySignalsAsync(
        string? conceptName = null,
        string? agentName = null,
        DateTimeOffset? since = null,
        int limit = 50,
        CancellationToken ct = default)
    {
        // Build a parameterised WHERE clause matching only the filters that were supplied —
        // each filter is opt-in so callers can compose narrow or wide queries without forking SQL.
        var conditions = new List<string>();
        var parameters = new List<object>();

        if (!string.IsNullOrEmpty(conceptName))
        {
            conditions.Add($"concept_name = ${parameters.Count + 1}");
            parameters.Add(conceptName);
        }
        if (!string.IsNullOrEmpty(agentName))
        {
            conditions.Add($"source = ${parameters.Count + 1}");
            parameters.Add(agentName);
        }
        if (since.HasValue)
        {
            conditions.Add($"at >= ${parameters.Count + 1}");
            parameters.Add(since.Value);
        }

        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : string.Empty;
        var limitParam = $"${parameters.Count + 1}";
        parameters.Add(limit);

        var sql = $"""
            SELECT run_id, correlation_id, source, signal_kind, level, error_type, is_transient, at, concept_name, attribution_path
            FROM vais_run_health_signals
            {where}
            ORDER BY at DESC
            LIMIT {limitParam}
            """;

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var p in parameters)
            cmd.Parameters.AddWithValue(p);

        var result = new List<RunHealthSignalRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var runId = reader.GetString(0);
            var correlationId = reader.IsDBNull(1) ? null : reader.GetString(1);
            var source = reader.GetString(2);
            var kind = Enum.TryParse<RunHealthSignalKind>(reader.GetString(3), out var k) ? k : RunHealthSignalKind.TurnFailed;
            var level = (FailureLevel)reader.GetInt16(4);
            var errorType = reader.IsDBNull(5) ? null : reader.GetString(5);
            var isTransient = reader.GetBoolean(6);
            var at = reader.GetFieldValue<DateTimeOffset>(7);
            var conceptVal = reader.IsDBNull(8) ? null : reader.GetString(8);
            var attributionPath = reader.IsDBNull(9) ? null : reader.GetString(9);
            result.Add(new RunHealthSignalRecord(
                runId, correlationId, source, kind, level, errorType, isTransient, at,
                ConceptName: conceptVal, AttributionPath: attributionPath));
        }
        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RunHealthRunSummary>> ListDegradedRunsAsync(
        FailureLevel minLevel = FailureLevel.Warning,
        DateTimeOffset? since = null,
        int limit = 50,
        CancellationToken ct = default)
    {
        var effectiveSince = since ?? DateTimeOffset.UtcNow - TimeSpan.FromHours(24);
        // GROUP BY run_id; filter rows with level >= minLevel; return aggregates.
        const string sql = """
            SELECT run_id, MAX(level) AS worst_level, COUNT(*) AS signal_count, MAX(at) AS latest_at
            FROM vais_run_health_signals
            WHERE at >= $1
            GROUP BY run_id
            HAVING MAX(level) >= $2
            ORDER BY MAX(at) DESC
            LIMIT $3
            """;

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue(effectiveSince);
        cmd.Parameters.AddWithValue((short)minLevel);
        cmd.Parameters.AddWithValue(limit);

        var result = new List<RunHealthRunSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new RunHealthRunSummary(
                RunId: reader.GetString(0),
                WorstLevel: (FailureLevel)reader.GetInt16(1),
                SignalCount: (int)reader.GetInt64(2),
                LatestAt: reader.GetFieldValue<DateTimeOffset>(3)));
        }
        return result;
    }
}
