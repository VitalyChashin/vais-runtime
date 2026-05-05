// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging;
using Npgsql;

namespace Vais.Agents.Observability.RunStore;

internal sealed class PostgresRunStore : IRunStore
{
    private readonly string _connectionString;
    private readonly ILogger<PostgresRunStore> _logger;

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS vais_graph_runs (
            run_id        TEXT PRIMARY KEY,
            graph_id      TEXT NOT NULL,
            status        TEXT NOT NULL DEFAULT 'running',
            started_at    TIMESTAMPTZ NOT NULL,
            ended_at      TIMESTAMPTZ,
            duration_ms   BIGINT,
            super_steps   INT NOT NULL DEFAULT 0,
            error         TEXT,
            created_at    TIMESTAMPTZ NOT NULL DEFAULT now());
        CREATE INDEX IF NOT EXISTS idx_vais_graph_runs_graph_id ON vais_graph_runs(graph_id, started_at DESC);
        CREATE INDEX IF NOT EXISTS idx_vais_graph_runs_created_at ON vais_graph_runs(created_at);
        CREATE TABLE IF NOT EXISTS vais_graph_run_nodes (
            run_id        TEXT NOT NULL,
            node_id       TEXT NOT NULL,
            node_kind     TEXT NOT NULL,
            agent_id      TEXT,
            status        TEXT NOT NULL DEFAULT 'running',
            started_at    TIMESTAMPTZ NOT NULL,
            ended_at      TIMESTAMPTZ,
            duration_ms   BIGINT,
            input_text    TEXT,
            output_text   TEXT,
            input_tokens  INT NOT NULL DEFAULT 0,
            output_tokens INT NOT NULL DEFAULT 0,
            error         TEXT,
            edges_taken   TEXT[],
            PRIMARY KEY (run_id, node_id));
        """;

    public PostgresRunStore(string connectionString, ILogger<PostgresRunStore> logger)
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
        _logger.LogDebug("RunStore schema applied.");
    }

    public async Task StartRunAsync(string runId, string graphId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO vais_graph_runs (run_id, graph_id, status, started_at)
            VALUES ($1, $2, 'running', now())
            ON CONFLICT (run_id) DO NOTHING
            """;
        cmd.Parameters.AddWithValue(runId);
        cmd.Parameters.AddWithValue(graphId);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task CompleteRunAsync(string runId, int superSteps, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE vais_graph_runs
            SET status = 'completed', ended_at = now(),
                duration_ms = EXTRACT(EPOCH FROM (now() - started_at))::BIGINT * 1000,
                super_steps = $2
            WHERE run_id = $1
            """;
        cmd.Parameters.AddWithValue(runId);
        cmd.Parameters.AddWithValue(superSteps);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task FailRunAsync(string runId, string error, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE vais_graph_runs
            SET status = 'failed', ended_at = now(),
                duration_ms = EXTRACT(EPOCH FROM (now() - started_at))::BIGINT * 1000,
                error = $2
            WHERE run_id = $1
            """;
        cmd.Parameters.AddWithValue(runId);
        cmd.Parameters.AddWithValue(error);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task InterruptRunAsync(string runId, string interruptId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE vais_graph_runs
            SET status = 'interrupted', error = $2
            WHERE run_id = $1
            """;
        cmd.Parameters.AddWithValue(runId);
        cmd.Parameters.AddWithValue(interruptId);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task StartNodeAsync(string runId, string nodeId, string nodeKind, string? agentId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO vais_graph_run_nodes (run_id, node_id, node_kind, agent_id, status, started_at)
            VALUES ($1, $2, $3, $4, 'running', now())
            ON CONFLICT (run_id, node_id) DO UPDATE
                SET status = 'running', started_at = now(), agent_id = EXCLUDED.agent_id
            """;
        cmd.Parameters.AddWithValue(runId);
        cmd.Parameters.AddWithValue(nodeId);
        cmd.Parameters.AddWithValue(nodeKind);
        cmd.Parameters.AddWithValue((object?)agentId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task CompleteNodeAsync(string runId, string nodeId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE vais_graph_run_nodes
            SET status = 'completed', ended_at = now(),
                duration_ms = EXTRACT(EPOCH FROM (now() - started_at))::BIGINT * 1000
            WHERE run_id = $1 AND node_id = $2
            """;
        cmd.Parameters.AddWithValue(runId);
        cmd.Parameters.AddWithValue(nodeId);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task RecordNodeInvocationAsync(string runId, string nodeId, string agentId,
        string inputText, string outputText, int inputTokens, int outputTokens, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE vais_graph_run_nodes
            SET agent_id = $3, input_text = $4, output_text = $5,
                input_tokens = $6, output_tokens = $7
            WHERE run_id = $1 AND node_id = $2
            """;
        cmd.Parameters.AddWithValue(runId);
        cmd.Parameters.AddWithValue(nodeId);
        cmd.Parameters.AddWithValue(agentId);
        cmd.Parameters.AddWithValue(inputText);
        cmd.Parameters.AddWithValue(outputText);
        cmd.Parameters.AddWithValue(inputTokens);
        cmd.Parameters.AddWithValue(outputTokens);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task RecordEdgeAsync(string runId, string fromNodeId, string toNodeId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE vais_graph_run_nodes
            SET edges_taken = array_append(COALESCE(edges_taken, '{}'), $3)
            WHERE run_id = $1 AND node_id = $2
            """;
        cmd.Parameters.AddWithValue(runId);
        cmd.Parameters.AddWithValue(fromNodeId);
        cmd.Parameters.AddWithValue(toNodeId);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteRunsOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var nodeCmd = conn.CreateCommand();
        nodeCmd.CommandText = """
            DELETE FROM vais_graph_run_nodes
            WHERE run_id IN (SELECT run_id FROM vais_graph_runs WHERE created_at < $1)
            """;
        nodeCmd.Parameters.AddWithValue(cutoff);
        await nodeCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await using var runCmd = conn.CreateCommand();
        runCmd.CommandText = "DELETE FROM vais_graph_runs WHERE created_at < $1";
        runCmd.Parameters.AddWithValue(cutoff);
        var deleted = await runCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (deleted > 0)
            _logger.LogInformation("RunStore pruned {Count} runs older than {Cutoff:u}.", deleted, cutoff);
    }

    public async Task<IReadOnlyList<PipelineRun>> ListRunsAsync(string graphId, RunStatus? status = null,
        DateTimeOffset? since = null, DateTimeOffset? until = null, int limit = 20, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();

        var conditions = new List<string> { "graph_id = $1" };
        var paramIdx = 2;
        if (status.HasValue) conditions.Add($"status = ${paramIdx++}");
        if (since.HasValue) conditions.Add($"started_at >= ${paramIdx++}");
        if (until.HasValue) conditions.Add($"started_at <= ${paramIdx++}");

        cmd.CommandText = $"""
            SELECT run_id, graph_id, status, started_at, ended_at, duration_ms, super_steps, error
            FROM vais_graph_runs
            WHERE {string.Join(" AND ", conditions)}
            ORDER BY started_at DESC
            LIMIT ${paramIdx}
            """;

        cmd.Parameters.AddWithValue(graphId);
        if (status.HasValue) cmd.Parameters.AddWithValue(StatusToString(status.Value));
        if (since.HasValue) cmd.Parameters.AddWithValue(since.Value);
        if (until.HasValue) cmd.Parameters.AddWithValue(until.Value);
        cmd.Parameters.AddWithValue(limit);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<PipelineRun>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            result.Add(ReadPipelineRun(reader));
        return result;
    }

    public async Task<PipelineRun?> GetRunAsync(string runId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT run_id, graph_id, status, started_at, ended_at, duration_ms, super_steps, error
            FROM vais_graph_runs WHERE run_id = $1
            """;
        cmd.Parameters.AddWithValue(runId);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadPipelineRun(reader) : null;
    }

    public async Task<IReadOnlyList<NodeExecution>> GetNodesAsync(string runId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT run_id, node_id, node_kind, agent_id, status, started_at, ended_at,
                   duration_ms, input_text, output_text, input_tokens, output_tokens, error, edges_taken
            FROM vais_graph_run_nodes WHERE run_id = $1 ORDER BY started_at
            """;
        cmd.Parameters.AddWithValue(runId);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<NodeExecution>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            result.Add(ReadNodeExecution(reader));
        return result;
    }

    public async Task<NodeExecution?> GetNodeAsync(string runId, string nodeId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT run_id, node_id, node_kind, agent_id, status, started_at, ended_at,
                   duration_ms, input_text, output_text, input_tokens, output_tokens, error, edges_taken
            FROM vais_graph_run_nodes WHERE run_id = $1 AND node_id = $2
            """;
        cmd.Parameters.AddWithValue(runId);
        cmd.Parameters.AddWithValue(nodeId);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadNodeExecution(reader) : null;
    }

    public async Task<IReadOnlyList<NodeExecution>> ListNodeExecutionsByAgentAsync(string agentId,
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
            SELECT run_id, node_id, node_kind, agent_id, status, started_at, ended_at,
                   duration_ms, input_text, output_text, input_tokens, output_tokens, error, edges_taken
            FROM vais_graph_run_nodes
            WHERE {string.Join(" AND ", conditions)}
            ORDER BY started_at DESC
            LIMIT ${paramIdx}
            """;

        cmd.Parameters.AddWithValue(agentId);
        if (since.HasValue) cmd.Parameters.AddWithValue(since.Value);
        if (until.HasValue) cmd.Parameters.AddWithValue(until.Value);
        cmd.Parameters.AddWithValue(limit);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<NodeExecution>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            result.Add(ReadNodeExecution(reader));
        return result;
    }

    private async ValueTask<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return conn;
    }

    private static PipelineRun ReadPipelineRun(NpgsqlDataReader r) =>
        new(r.GetString(0), r.GetString(1),
            StatusFromString(r.GetString(2)),
            r.GetFieldValue<DateTimeOffset>(3),
            r.IsDBNull(4) ? null : r.GetFieldValue<DateTimeOffset>(4),
            r.IsDBNull(5) ? null : r.GetInt64(5),
            r.GetInt32(6),
            r.IsDBNull(7) ? null : r.GetString(7));

    private static NodeExecution ReadNodeExecution(NpgsqlDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2),
            r.IsDBNull(3) ? null : r.GetString(3),
            StatusFromString(r.GetString(4)),
            r.GetFieldValue<DateTimeOffset>(5),
            r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6),
            r.IsDBNull(7) ? null : r.GetInt64(7),
            r.IsDBNull(8) ? null : r.GetString(8),
            r.IsDBNull(9) ? null : r.GetString(9),
            r.GetInt32(10),
            r.GetInt32(11),
            r.IsDBNull(12) ? null : r.GetString(12),
            r.IsDBNull(13) ? null : r.GetFieldValue<string[]>(13));

    private static string StatusToString(RunStatus s) => s switch
    {
        RunStatus.Running => "running",
        RunStatus.Completed => "completed",
        RunStatus.Failed => "failed",
        RunStatus.Interrupted => "interrupted",
        _ => "running",
    };

    private static RunStatus StatusFromString(string s) => s switch
    {
        "completed" => RunStatus.Completed,
        "failed" => RunStatus.Failed,
        "interrupted" => RunStatus.Interrupted,
        _ => RunStatus.Running,
    };
}
