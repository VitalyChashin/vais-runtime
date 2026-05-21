// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Text.Json;
using Npgsql;

namespace Vais.Agents.Persistence.Postgres;

/// <summary>
/// Postgres-backed <see cref="IGraphCheckpointer"/> using raw Npgsql. The lower-cost,
/// silo-free alternative to <c>OrleansCheckpointer</c>: a self-hosted
/// <c>MafGraphOrchestrator</c> / <c>InProcessGraphOrchestrator</c> can persist + resume
/// graph runs against Postgres directly, without an Orleans grain backing.
/// </summary>
/// <remarks>
/// <para>
/// Latest-only: one row per <see cref="GraphCheckpoint.RunId"/> (PK), upserted on each
/// super-step boundary — the same shape as <c>OrleansCheckpointer</c> (no checkpoint
/// history; LangGraph-style <c>put_writes</c> is out of scope per <see cref="IGraphCheckpointer"/>).
/// The whole checkpoint is stored as a <c>jsonb</c> blob (the authoritative value on load);
/// the promoted columns are for debuggability / ad-hoc queries.
/// </para>
/// <para>
/// Schema is applied automatically + idempotently on first use (and via
/// <see cref="InitializeAsync"/>), so consumers don't need to run a migration script.
/// </para>
/// </remarks>
public sealed class PostgresGraphCheckpointer : IGraphCheckpointer
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS vais_graph_checkpoints (
            run_id               TEXT PRIMARY KEY,
            graph_id             TEXT NOT NULL,
            graph_version        TEXT NOT NULL,
            checkpoint_json      JSONB NOT NULL,
            is_complete          BOOLEAN NOT NULL,
            super_step           INT NOT NULL,
            pending_interrupt_id TEXT,
            saved_at             TIMESTAMPTZ NOT NULL DEFAULT now());
        CREATE INDEX IF NOT EXISTS idx_vais_graph_checkpoints_graph_id ON vais_graph_checkpoints(graph_id);
        """;

    private readonly NpgsqlDataSource _ds;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private bool _initialized;

    /// <param name="dataSource">Pooled Npgsql data source. Caller owns lifetime.</param>
    public PostgresGraphCheckpointer(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _ds = dataSource;
    }

    /// <summary>Apply the schema (idempotent). Called automatically on first use; expose for fail-fast startup.</summary>
    public Task InitializeAsync(CancellationToken cancellationToken = default)
        => EnsureInitializedAsync(cancellationToken).AsTask();

    /// <inheritdoc />
    public async ValueTask SaveAsync(GraphCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var conn = await _ds.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO vais_graph_checkpoints
                (run_id, graph_id, graph_version, checkpoint_json, is_complete, super_step, pending_interrupt_id, saved_at)
            VALUES
                (@run, @graph, @ver, @json::jsonb, @complete, @step, @interrupt, now())
            ON CONFLICT (run_id) DO UPDATE SET
                graph_id             = EXCLUDED.graph_id,
                graph_version        = EXCLUDED.graph_version,
                checkpoint_json      = EXCLUDED.checkpoint_json,
                is_complete          = EXCLUDED.is_complete,
                super_step           = EXCLUDED.super_step,
                pending_interrupt_id = EXCLUDED.pending_interrupt_id,
                saved_at             = now()
            """;
        cmd.Parameters.AddWithValue("run", checkpoint.RunId);
        cmd.Parameters.AddWithValue("graph", checkpoint.GraphId);
        cmd.Parameters.AddWithValue("ver", checkpoint.GraphVersion);
        cmd.Parameters.AddWithValue("json", JsonSerializer.Serialize(checkpoint));
        cmd.Parameters.AddWithValue("complete", checkpoint.IsComplete);
        cmd.Parameters.AddWithValue("step", checkpoint.SuperStepIndex);
        cmd.Parameters.AddWithValue("interrupt", (object?)checkpoint.PendingInterruptId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<GraphCheckpoint?> LoadAsync(string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var conn = await _ds.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT checkpoint_json FROM vais_graph_checkpoints WHERE run_id = @run";
        cmd.Parameters.AddWithValue("run", runId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        var json = reader.GetString(0);
        var checkpoint = JsonSerializer.Deserialize<GraphCheckpoint>(json);
        if (checkpoint is null)
        {
            throw new InvalidOperationException(
                $"Stored checkpoint for run '{runId}' deserialised to null — storage corruption?");
        }
        return checkpoint;
    }

    /// <inheritdoc />
    public async ValueTask DeleteAsync(string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var conn = await _ds.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM vais_graph_checkpoints WHERE run_id = @run";
        cmd.Parameters.AddWithValue("run", runId);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask EnsureInitializedAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _initialized))
        {
            return;
        }
        await _initGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }
            await using var conn = await _ds.OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = Schema;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            Volatile.Write(ref _initialized, true);
        }
        finally
        {
            _initGate.Release();
        }
    }
}
