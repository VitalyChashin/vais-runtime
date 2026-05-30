// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using Npgsql;
using Vais.Agents.Eval;

namespace Vais.Agents.Persistence.Postgres;

/// <summary>
/// Postgres-backed <see cref="IEvalResultStore"/> using raw Npgsql.
/// Schema: <c>vais_eval_runs</c> + <c>vais_eval_case_results</c> (see Migrations/eval_results_tables.sql).
/// </summary>
public sealed class PostgresEvalResultStore : IEvalResultStore
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    private readonly NpgsqlDataSource _ds;

    /// <param name="dataSource">Pooled Npgsql data source. Caller owns lifetime.</param>
    public PostgresEvalResultStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _ds = dataSource;
    }

    /// <inheritdoc/>
    public async ValueTask AppendRunAsync(EvalRunSummary run, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        await using var conn = await _ds.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO vais_eval_runs
                (eval_run_id, suite_name, suite_version, started_at, completed_at,
                 status, total_cases, passed_cases, failed_cases, source, window_start, window_end)
            VALUES
                (@id, @suite, @ver, @started, @completed,
                 @status, @total, @passed, @failed, @source, @wstart, @wend)
            ON CONFLICT (eval_run_id) DO UPDATE SET
                completed_at  = EXCLUDED.completed_at,
                status        = EXCLUDED.status,
                total_cases   = EXCLUDED.total_cases,
                passed_cases  = EXCLUDED.passed_cases,
                failed_cases  = EXCLUDED.failed_cases,
                source        = EXCLUDED.source,
                window_start  = EXCLUDED.window_start,
                window_end    = EXCLUDED.window_end
            """;
        cmd.Parameters.AddWithValue("id", run.EvalRunId);
        cmd.Parameters.AddWithValue("suite", run.SuiteName);
        cmd.Parameters.AddWithValue("ver", run.SuiteVersion);
        cmd.Parameters.AddWithValue("started", run.StartedAt);
        cmd.Parameters.AddWithValue("completed", (object?)run.CompletedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("status", (int)run.Status);
        cmd.Parameters.AddWithValue("total", run.TotalCases);
        cmd.Parameters.AddWithValue("passed", run.PassedCases);
        cmd.Parameters.AddWithValue("failed", run.FailedCases);
        cmd.Parameters.AddWithValue("source", run.Source);
        cmd.Parameters.AddWithValue("wstart", (object?)run.WindowStart ?? DBNull.Value);
        cmd.Parameters.AddWithValue("wend", (object?)run.WindowEnd ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask AppendCaseResultAsync(EvalCaseResultRecord result, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        var assertionJson = JsonSerializer.Serialize(result.AssertionResults, JsonOpts);
        await using var conn = await _ds.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        var breakdownJson = result.MechanicalBreakdown is null
            ? null
            : JsonSerializer.Serialize(result.MechanicalBreakdown, JsonOpts);
        cmd.CommandText = """
            INSERT INTO vais_eval_case_results
                (eval_run_id, case_id, agent_run_id, started_at, completed_at,
                 status, response_text, assertion_results, production_run_id,
                 mechanical_level, mechanical_failure_count, mechanical_breakdown)
            VALUES
                (@run, @case, @agent, @started, @completed,
                 @status, @response, @assertions::jsonb, @prodrun,
                 @mechlevel, @mechcount, @mechbreakdown::jsonb)
            ON CONFLICT (eval_run_id, case_id) DO UPDATE SET
                agent_run_id              = EXCLUDED.agent_run_id,
                completed_at              = EXCLUDED.completed_at,
                status                    = EXCLUDED.status,
                response_text             = EXCLUDED.response_text,
                assertion_results         = EXCLUDED.assertion_results,
                production_run_id         = EXCLUDED.production_run_id,
                mechanical_level          = EXCLUDED.mechanical_level,
                mechanical_failure_count  = EXCLUDED.mechanical_failure_count,
                mechanical_breakdown      = EXCLUDED.mechanical_breakdown
            """;
        cmd.Parameters.AddWithValue("run", result.EvalRunId);
        cmd.Parameters.AddWithValue("case", result.CaseId);
        cmd.Parameters.AddWithValue("agent", (object?)result.AgentRunId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("started", result.StartedAt);
        cmd.Parameters.AddWithValue("completed", (object?)result.CompletedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("status", (int)result.Status);
        cmd.Parameters.AddWithValue("response", (object?)result.ResponseText ?? DBNull.Value);
        cmd.Parameters.AddWithValue("assertions", assertionJson);
        cmd.Parameters.AddWithValue("prodrun", (object?)result.ProductionRunId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("mechlevel", (short)result.MechanicalLevel);
        cmd.Parameters.AddWithValue("mechcount", result.MechanicalFailureCount);
        cmd.Parameters.AddWithValue("mechbreakdown", (object?)breakdownJson ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<EvalRunSummary>> ListRunsAsync(
        string? suiteName = null,
        int limit = 50,
        string? source = null,
        CancellationToken ct = default)
    {
        await using var conn = await _ds.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();

        var where = new List<string>();
        if (suiteName is not null) where.Add("suite_name = @suite");
        if (source is not null) where.Add("source = @source");
        var whereClause = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : string.Empty;

        cmd.CommandText = $"""
            SELECT eval_run_id, suite_name, suite_version, started_at, completed_at,
                   status, total_cases, passed_cases, failed_cases, source, window_start, window_end
            FROM vais_eval_runs
            {whereClause}
            ORDER BY started_at DESC
            LIMIT @limit
            """;
        if (suiteName is not null) cmd.Parameters.AddWithValue("suite", suiteName);
        if (source is not null) cmd.Parameters.AddWithValue("source", source);
        cmd.Parameters.AddWithValue("limit", limit);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var results = new List<EvalRunSummary>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            results.Add(ReadRunSummary(reader));
        return results;
    }

    /// <inheritdoc/>
    public async ValueTask<EvalRunDetail?> GetRunAsync(string evalRunId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evalRunId);

        await using var conn = await _ds.OpenConnectionAsync(ct).ConfigureAwait(false);

        EvalRunSummary? summary = null;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT eval_run_id, suite_name, suite_version, started_at, completed_at,
                       status, total_cases, passed_cases, failed_cases, source, window_start, window_end
                FROM vais_eval_runs
                WHERE eval_run_id = @id
                """;
            cmd.Parameters.AddWithValue("id", evalRunId);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
                summary = ReadRunSummary(reader);
        }

        if (summary is null) return null;

        var cases = new List<EvalCaseResultRecord>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT eval_run_id, case_id, agent_run_id, started_at, completed_at,
                       status, response_text, assertion_results, production_run_id,
                       mechanical_level, mechanical_failure_count, mechanical_breakdown
                FROM vais_eval_case_results
                WHERE eval_run_id = @id
                ORDER BY started_at ASC
                """;
            cmd.Parameters.AddWithValue("id", evalRunId);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                cases.Add(ReadCaseResult(reader));
        }

        return new EvalRunDetail(summary, cases);
    }

    // ── Readers ───────────────────────────────────────────────────────────────

    private static EvalRunSummary ReadRunSummary(NpgsqlDataReader r) => new(
        EvalRunId:     r.GetString(0),
        SuiteName:     r.GetString(1),
        SuiteVersion:  r.GetString(2),
        StartedAt:     r.GetFieldValue<DateTimeOffset>(3),
        CompletedAt:   r.IsDBNull(4) ? null : r.GetFieldValue<DateTimeOffset>(4),
        Status:        (EvalRunStatus)r.GetInt32(5),
        TotalCases:    r.GetInt32(6),
        PassedCases:   r.GetInt32(7),
        FailedCases:   r.GetInt32(8),
        Source:        r.GetString(9),
        WindowStart:   r.IsDBNull(10) ? null : r.GetFieldValue<DateTimeOffset>(10),
        WindowEnd:     r.IsDBNull(11) ? null : r.GetFieldValue<DateTimeOffset>(11));

    private static EvalCaseResultRecord ReadCaseResult(NpgsqlDataReader r)
    {
        var assertionJson = r.GetString(7);
        var assertions = JsonSerializer.Deserialize<List<EvalAssertionResultRecord>>(assertionJson, JsonOpts)
            ?? [];

        // Columns 9-11 are the CS-1 mechanical axis additions (may be absent in older rows → use IsDBNull).
        var mechLevel = r.FieldCount > 9 && !r.IsDBNull(9) ? (FailureLevel)r.GetInt16(9) : FailureLevel.Default;
        var mechCount = r.FieldCount > 10 && !r.IsDBNull(10) ? r.GetInt32(10) : 0;
        IReadOnlyDictionary<string, int>? mechBreakdown = null;
        if (r.FieldCount > 11 && !r.IsDBNull(11))
        {
            var json = r.GetString(11);
            mechBreakdown = JsonSerializer.Deserialize<Dictionary<string, int>>(json, JsonOpts);
        }

        return new EvalCaseResultRecord(
            EvalRunId:               r.GetString(0),
            CaseId:                  r.GetString(1),
            AgentRunId:              r.IsDBNull(2) ? null : r.GetString(2),
            StartedAt:               r.GetFieldValue<DateTimeOffset>(3),
            CompletedAt:             r.IsDBNull(4) ? null : r.GetFieldValue<DateTimeOffset>(4),
            Status:                  (EvalCaseStatus)r.GetInt32(5),
            ResponseText:            r.IsDBNull(6) ? null : r.GetString(6),
            AssertionResults:        assertions,
            ProductionRunId:         r.IsDBNull(8) ? null : r.GetString(8),
            MechanicalLevel:         mechLevel,
            MechanicalFailureCount:  mechCount,
            MechanicalBreakdown:     mechBreakdown);
    }
}
