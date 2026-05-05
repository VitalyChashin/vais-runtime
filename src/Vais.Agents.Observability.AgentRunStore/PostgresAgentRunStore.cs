// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging;
using Npgsql;

namespace Vais.Agents.Observability.AgentRunStore;

internal sealed class PostgresAgentRunStore : IAgentRunStore
{
    private readonly string _connectionString;
    private readonly ILogger<PostgresAgentRunStore> _logger;

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS vais_agent_runs (
            agent_run_id  TEXT PRIMARY KEY,
            agent_id      TEXT NOT NULL,
            status        TEXT NOT NULL DEFAULT 'running',
            started_at    TIMESTAMPTZ NOT NULL,
            ended_at      TIMESTAMPTZ,
            duration_ms   BIGINT,
            input_text    TEXT,
            output_text   TEXT,
            input_tokens  INT NOT NULL DEFAULT 0,
            output_tokens INT NOT NULL DEFAULT 0,
            error         TEXT,
            correlation_id TEXT,
            user_id       TEXT,
            tenant_id     TEXT,
            created_at    TIMESTAMPTZ NOT NULL DEFAULT now());
        CREATE INDEX IF NOT EXISTS idx_vais_agent_runs_agent_id ON vais_agent_runs(agent_id, started_at DESC);
        CREATE INDEX IF NOT EXISTS idx_vais_agent_runs_created_at ON vais_agent_runs(created_at);
        """;

    public PostgresAgentRunStore(string connectionString, ILogger<PostgresAgentRunStore> logger)
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
        _logger.LogDebug("AgentRunStore schema applied.");
    }

    public async Task StartRunAsync(string agentRunId, string agentId, string? inputText,
        string? userId, string? tenantId, string? correlationId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO vais_agent_runs
                (agent_run_id, agent_id, status, started_at, input_text, user_id, tenant_id, correlation_id)
            VALUES ($1, $2, 'running', now(), $3, $4, $5, $6)
            ON CONFLICT (agent_run_id) DO NOTHING
            """;
        cmd.Parameters.AddWithValue(agentRunId);
        cmd.Parameters.AddWithValue(agentId);
        cmd.Parameters.AddWithValue((object?)inputText ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)userId ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)tenantId ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)correlationId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task CompleteRunAsync(string agentRunId, string? outputText,
        int inputTokens, int outputTokens, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE vais_agent_runs
            SET status = 'completed', ended_at = now(),
                duration_ms = EXTRACT(EPOCH FROM (now() - started_at))::BIGINT * 1000,
                output_text = $2, input_tokens = $3, output_tokens = $4
            WHERE agent_run_id = $1
            """;
        cmd.Parameters.AddWithValue(agentRunId);
        cmd.Parameters.AddWithValue((object?)outputText ?? DBNull.Value);
        cmd.Parameters.AddWithValue(inputTokens);
        cmd.Parameters.AddWithValue(outputTokens);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task FailRunAsync(string agentRunId, string error, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE vais_agent_runs
            SET status = 'failed', ended_at = now(),
                duration_ms = EXTRACT(EPOCH FROM (now() - started_at))::BIGINT * 1000,
                error = $2
            WHERE agent_run_id = $1
            """;
        cmd.Parameters.AddWithValue(agentRunId);
        cmd.Parameters.AddWithValue(error);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteRunsOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM vais_agent_runs WHERE created_at < $1";
        cmd.Parameters.AddWithValue(cutoff);
        var deleted = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (deleted > 0)
            _logger.LogInformation("AgentRunStore pruned {Count} runs older than {Cutoff:u}.", deleted, cutoff);
    }

    public async Task<IReadOnlyList<AgentRun>> ListRunsAsync(string agentId,
        DateTimeOffset? since = null, DateTimeOffset? until = null, int limit = 20,
        CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();

        var conditions = new List<string> { "agent_id = $1" };
        var paramIdx = 2;
        if (since.HasValue) conditions.Add($"started_at >= ${paramIdx++}");
        if (until.HasValue) conditions.Add($"started_at <= ${paramIdx++}");

        cmd.CommandText = $"""
            SELECT agent_run_id, agent_id, status, started_at, ended_at, duration_ms,
                   input_text, output_text, input_tokens, output_tokens, error,
                   correlation_id, user_id, tenant_id
            FROM vais_agent_runs
            WHERE {string.Join(" AND ", conditions)}
            ORDER BY started_at DESC
            LIMIT ${paramIdx}
            """;

        cmd.Parameters.AddWithValue(agentId);
        if (since.HasValue) cmd.Parameters.AddWithValue(since.Value);
        if (until.HasValue) cmd.Parameters.AddWithValue(until.Value);
        cmd.Parameters.AddWithValue(limit);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<AgentRun>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            result.Add(ReadAgentRun(reader));
        return result;
    }

    public async Task<AgentRun?> GetRunAsync(string agentRunId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT agent_run_id, agent_id, status, started_at, ended_at, duration_ms,
                   input_text, output_text, input_tokens, output_tokens, error,
                   correlation_id, user_id, tenant_id
            FROM vais_agent_runs WHERE agent_run_id = $1
            """;
        cmd.Parameters.AddWithValue(agentRunId);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadAgentRun(reader) : null;
    }

    private async ValueTask<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return conn;
    }

    private static AgentRun ReadAgentRun(NpgsqlDataReader r) =>
        new(r.GetString(0), r.GetString(1),
            StatusFromString(r.GetString(2)),
            r.GetFieldValue<DateTimeOffset>(3),
            r.IsDBNull(4) ? null : r.GetFieldValue<DateTimeOffset>(4),
            r.IsDBNull(5) ? null : r.GetInt64(5),
            r.IsDBNull(6) ? null : r.GetString(6),
            r.IsDBNull(7) ? null : r.GetString(7),
            r.GetInt32(8),
            r.GetInt32(9),
            r.IsDBNull(10) ? null : r.GetString(10),
            r.IsDBNull(11) ? null : r.GetString(11),
            r.IsDBNull(12) ? null : r.GetString(12),
            r.IsDBNull(13) ? null : r.GetString(13));

    private static AgentRunStatus StatusFromString(string s) => s switch
    {
        "completed" => AgentRunStatus.Completed,
        "failed" => AgentRunStatus.Failed,
        _ => AgentRunStatus.Running,
    };
}
