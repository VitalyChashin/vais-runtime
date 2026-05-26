// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Vais.Agents.Observability.InterceptorTeeStore;

/// <summary>
/// Postgres-backed <see cref="IInterceptorTeeStore"/> for Plan D. Schema auto-creates on
/// first <see cref="InitializeAsync"/> call; appends are idempotent on
/// <see cref="TrajectoryEvent.EventId"/>; queries scan by indexed columns
/// (<c>agent_id, timestamp DESC</c>) plus optional filters AND-combined inline.
/// </summary>
/// <remarks>
/// Mirrors <c>PostgresAgentRunStore</c> exactly — same connection-per-call pattern, same
/// embedded CREATE-IF-NOT-EXISTS schema, same prune-on-startup retention via the companion
/// <see cref="InterceptorTeeStoreInitializer"/>. The argument-shape map is persisted as
/// JSONB so queries can join on shape keys in future iterations.
/// </remarks>
public sealed class PostgresInterceptorTeeStore : IInterceptorTeeStore
{
    private readonly string _connectionString;
    private readonly ILogger<PostgresInterceptorTeeStore> _logger;

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS vais_trajectory_events (
            event_id          TEXT PRIMARY KEY,
            timestamp         TIMESTAMPTZ NOT NULL,
            event_name        TEXT NOT NULL,
            operation         TEXT NOT NULL,
            agent_id          TEXT,
            run_id            TEXT,
            concept_name      TEXT,
            transport         TEXT,
            arguments_shape   JSONB,
            outcome_kind      TEXT,
            outcome_error     TEXT,
            ontology_version  TEXT,
            duration_ms       BIGINT,
            created_at        TIMESTAMPTZ NOT NULL DEFAULT now());
        CREATE INDEX IF NOT EXISTS idx_vais_traj_agent_time ON vais_trajectory_events(agent_id, timestamp DESC);
        CREATE INDEX IF NOT EXISTS idx_vais_traj_run ON vais_trajectory_events(run_id);
        CREATE INDEX IF NOT EXISTS idx_vais_traj_concept ON vais_trajectory_events(concept_name);
        CREATE INDEX IF NOT EXISTS idx_vais_traj_time ON vais_trajectory_events(timestamp DESC);
        """;

    /// <summary>Build the store. The connection string is held; connections open per call.</summary>
    public PostgresInterceptorTeeStore(string connectionString, ILogger<PostgresInterceptorTeeStore> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    /// <summary>Apply the schema (idempotent — uses <c>CREATE TABLE IF NOT EXISTS</c> + <c>CREATE INDEX IF NOT EXISTS</c>).</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = Schema;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _logger.LogDebug("InterceptorTeeStore schema applied.");
    }

    /// <summary>Prune events older than <paramref name="cutoff"/>. Called from the initializer at startup.</summary>
    public async Task DeleteEventsOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM vais_trajectory_events WHERE created_at < $1";
        cmd.Parameters.AddWithValue(cutoff);
        var deleted = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (deleted > 0)
            _logger.LogInformation("InterceptorTeeStore pruned {Count} trajectory events older than {Cutoff:u}.", deleted, cutoff);
    }

    /// <inheritdoc />
    public async ValueTask AppendAsync(TrajectoryEvent trajectoryEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trajectoryEvent);
        await using var conn = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO vais_trajectory_events
                (event_id, timestamp, event_name, operation, agent_id, run_id, concept_name,
                 transport, arguments_shape, outcome_kind, outcome_error, ontology_version, duration_ms)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9::jsonb, $10, $11, $12, $13)
            ON CONFLICT (event_id) DO NOTHING
            """;
        cmd.Parameters.AddWithValue(trajectoryEvent.EventId);
        cmd.Parameters.AddWithValue(trajectoryEvent.Timestamp);
        cmd.Parameters.AddWithValue(trajectoryEvent.EventName);
        cmd.Parameters.AddWithValue(trajectoryEvent.Operation.ToString());
        cmd.Parameters.AddWithValue((object?)trajectoryEvent.AgentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)trajectoryEvent.RunId ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)trajectoryEvent.ConceptName ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)trajectoryEvent.Transport ?? DBNull.Value);
        cmd.Parameters.AddWithValue(trajectoryEvent.ArgumentsShape is { Count: > 0 } shape
            ? JsonSerializer.Serialize(shape)
            : (object)DBNull.Value);
        cmd.Parameters.AddWithValue((object?)trajectoryEvent.Outcome?.Kind.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)trajectoryEvent.Outcome?.ErrorType ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)trajectoryEvent.OntologyVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue(trajectoryEvent.Duration.HasValue
            ? (object)(long)trajectoryEvent.Duration.Value.TotalMilliseconds
            : DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<TrajectoryEvent> QueryAsync(
        TrajectoryQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var conn = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();

        var conditions = new List<string>();
        var paramIdx = 1;
        void AddCondition(string column, object value)
        {
            conditions.Add($"{column} = ${paramIdx++}");
            cmd.Parameters.AddWithValue(value);
        }
        if (query.AgentId is { } a) AddCondition("agent_id", a);
        if (query.RunId is { } r) AddCondition("run_id", r);
        if (query.ConceptName is { } c) AddCondition("concept_name", c);
        if (query.Transport is { } t) AddCondition("transport", t);
        if (query.OutcomeKind is { } ok) AddCondition("outcome_kind", ok.ToString());
        if (query.Since is { } since)
        {
            conditions.Add($"timestamp >= ${paramIdx++}");
            cmd.Parameters.AddWithValue(since);
        }
        if (query.Until is { } until)
        {
            conditions.Add($"timestamp < ${paramIdx++}");
            cmd.Parameters.AddWithValue(until);
        }

        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : string.Empty;
        var limitSql = query.Limit.HasValue ? $" LIMIT ${paramIdx}" : string.Empty;
        if (query.Limit.HasValue) cmd.Parameters.AddWithValue(query.Limit.Value);

        cmd.CommandText = $"""
            SELECT event_id, timestamp, event_name, operation, agent_id, run_id, concept_name,
                   transport, arguments_shape, outcome_kind, outcome_error, ontology_version, duration_ms
            FROM vais_trajectory_events
            {where}
            ORDER BY timestamp DESC
            {limitSql}
            """;

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            yield return Read(reader);
    }

    private async ValueTask<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return conn;
    }

    private static TrajectoryEvent Read(NpgsqlDataReader r)
    {
        IReadOnlyDictionary<string, string>? shape = null;
        if (!r.IsDBNull(8))
        {
            var json = r.GetString(8);
            shape = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        }
        TrajectoryOutcome? outcome = null;
        if (!r.IsDBNull(9))
        {
            var kind = Enum.Parse<TrajectoryOutcomeKind>(r.GetString(9), ignoreCase: true);
            var err = r.IsDBNull(10) ? null : r.GetString(10);
            outcome = new TrajectoryOutcome(kind, err);
        }
        return new TrajectoryEvent
        {
            EventId = r.GetString(0),
            Timestamp = r.GetFieldValue<DateTimeOffset>(1),
            EventName = r.GetString(2),
            Operation = Enum.Parse<OntologyOperation>(r.GetString(3), ignoreCase: true),
            AgentId = r.IsDBNull(4) ? null : r.GetString(4),
            RunId = r.IsDBNull(5) ? null : r.GetString(5),
            ConceptName = r.IsDBNull(6) ? null : r.GetString(6),
            Transport = r.IsDBNull(7) ? null : r.GetString(7),
            ArgumentsShape = shape,
            Outcome = outcome,
            OntologyVersion = r.IsDBNull(11) ? null : r.GetString(11),
            Duration = r.IsDBNull(12) ? null : TimeSpan.FromMilliseconds(r.GetInt64(12)),
        };
    }
}
