// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Vais.Agents.Observability.RecipeProposalStore;

/// <summary>
/// Postgres-backed <see cref="IRecipeProposalStore"/> for Plan D. Schema auto-creates on
/// first <see cref="InitializeAsync"/> call; upserts are idempotent on
/// <see cref="RecipeProposal.ProposalId"/>; status transitions are atomic via UPDATE-with-WHERE.
/// Mirrors <c>PostgresInterceptorTeeStore</c>.
/// </summary>
/// <remarks>
/// The optional <c>highRiskApprovalCheck</c> delegate gates flips of high-risk proposals to
/// Approved (keeps this project Control-free, same shape as
/// <c>InMemoryRecipeProposalStore</c>). CompositionRoot wires the delegate to call into
/// <c>IApprovalStore</c> and throw <c>ApprovalRequiredException</c> when no matching approval
/// exists.
/// </remarks>
public sealed class PostgresRecipeProposalStore : IRecipeProposalStore
{
    private readonly string _connectionString;
    private readonly ILogger<PostgresRecipeProposalStore> _logger;
    private readonly Func<RecipeProposal, string, CancellationToken, ValueTask>? _highRiskApprovalCheck;

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS vais_recipe_proposals (
            proposal_id        TEXT PRIMARY KEY,
            kind               TEXT NOT NULL,
            concept            TEXT NOT NULL,
            body               TEXT NOT NULL,
            support            INT NOT NULL,
            confidence         DOUBLE PRECISION NOT NULL,
            source_trace_ids   JSONB NOT NULL,
            risk_level         TEXT NOT NULL,
            status             TEXT NOT NULL,
            created_at         TIMESTAMPTZ NOT NULL,
            reviewed_at        TIMESTAMPTZ,
            reviewer_id        TEXT,
            name               TEXT);
        CREATE INDEX IF NOT EXISTS idx_vais_recipe_status ON vais_recipe_proposals(status);
        CREATE INDEX IF NOT EXISTS idx_vais_recipe_concept ON vais_recipe_proposals(concept);
        CREATE INDEX IF NOT EXISTS idx_vais_recipe_created ON vais_recipe_proposals(created_at DESC);
        """;

    /// <summary>Build the store. The connection string is held; connections open per call.</summary>
    public PostgresRecipeProposalStore(
        string connectionString,
        ILogger<PostgresRecipeProposalStore> logger,
        Func<RecipeProposal, string, CancellationToken, ValueTask>? highRiskApprovalCheck = null)
    {
        _connectionString = connectionString;
        _logger = logger;
        _highRiskApprovalCheck = highRiskApprovalCheck;
    }

    /// <summary>Apply the schema (idempotent — uses <c>CREATE TABLE IF NOT EXISTS</c> + <c>CREATE INDEX IF NOT EXISTS</c>).</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = Schema;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _logger.LogDebug("RecipeProposalStore schema applied.");
    }

    /// <summary>Prune decided proposals older than <paramref name="cutoff"/>. Pending proposals are kept.</summary>
    public async Task DeleteDecidedProposalsOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM vais_recipe_proposals WHERE status <> 'Pending' AND reviewed_at < $1";
        cmd.Parameters.AddWithValue(cutoff);
        var deleted = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (deleted > 0)
            _logger.LogInformation("RecipeProposalStore pruned {Count} decided proposals older than {Cutoff:u}.", deleted, cutoff);
    }

    /// <inheritdoc />
    public async ValueTask UpsertAsync(RecipeProposal proposal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        await using var conn = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        // Preserve human-decision fields across re-induction: existing reviewed_at / reviewer_id
        // / status (if decided) are kept.
        cmd.CommandText = """
            INSERT INTO vais_recipe_proposals
                (proposal_id, kind, concept, body, support, confidence, source_trace_ids,
                 risk_level, status, created_at, reviewed_at, reviewer_id, name)
            VALUES ($1, $2, $3, $4, $5, $6, $7::jsonb, $8, $9, $10, $11, $12, $13)
            ON CONFLICT (proposal_id) DO UPDATE SET
                kind             = EXCLUDED.kind,
                concept          = EXCLUDED.concept,
                body             = EXCLUDED.body,
                support          = EXCLUDED.support,
                confidence       = EXCLUDED.confidence,
                source_trace_ids = EXCLUDED.source_trace_ids,
                risk_level       = EXCLUDED.risk_level,
                status           = CASE WHEN vais_recipe_proposals.status = 'Pending'
                                        THEN EXCLUDED.status
                                        ELSE vais_recipe_proposals.status END,
                name             = COALESCE(EXCLUDED.name, vais_recipe_proposals.name)
            """;
        cmd.Parameters.AddWithValue(proposal.ProposalId);
        cmd.Parameters.AddWithValue(proposal.Kind.ToString());
        cmd.Parameters.AddWithValue(proposal.Concept);
        cmd.Parameters.AddWithValue(proposal.Body);
        cmd.Parameters.AddWithValue(proposal.Support);
        cmd.Parameters.AddWithValue(proposal.Confidence);
        cmd.Parameters.AddWithValue(JsonSerializer.Serialize(proposal.SourceTraceIds));
        cmd.Parameters.AddWithValue(proposal.RiskLevel.ToString());
        cmd.Parameters.AddWithValue(proposal.Status.ToString());
        cmd.Parameters.AddWithValue(proposal.CreatedAt);
        cmd.Parameters.AddWithValue((object?)proposal.ReviewedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)proposal.ReviewerId ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)proposal.Name ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<RecipeProposal?> GetAsync(string proposalId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposalId);
        await using var conn = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"{SelectColumns} FROM vais_recipe_proposals WHERE proposal_id = $1";
        cmd.Parameters.AddWithValue(proposalId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RecipeProposal> ListAsync(
        RecipeProposalQuery query,
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
        if (query.Concept is { } c) AddCondition("concept", c);
        if (query.Kind is { } k) AddCondition("kind", k.ToString());
        if (query.Status is { } s) AddCondition("status", s.ToString());
        if (query.RiskLevel is { } r) AddCondition("risk_level", r.ToString());

        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : string.Empty;
        var limitSql = query.Limit.HasValue ? $" LIMIT ${paramIdx}" : string.Empty;
        if (query.Limit.HasValue) cmd.Parameters.AddWithValue(query.Limit.Value);

        cmd.CommandText = $"{SelectColumns} FROM vais_recipe_proposals {where} ORDER BY created_at DESC {limitSql}";

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            yield return Read(reader);
    }

    /// <inheritdoc />
    public async ValueTask<RecipeProposal?> DecideAsync(string proposalId, bool approve, string decidedBy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposalId);
        ArgumentNullException.ThrowIfNull(decidedBy);

        var current = await GetAsync(proposalId, cancellationToken).ConfigureAwait(false);
        if (current is null) return null;
        if (current.Status != RecipeProposalStatus.Pending) return current;

        if (approve && current.RiskLevel == RecipeProposalRiskLevel.High && _highRiskApprovalCheck is not null)
        {
            await _highRiskApprovalCheck(current, decidedBy, cancellationToken).ConfigureAwait(false);
        }

        var nextStatus = approve ? RecipeProposalStatus.Approved : RecipeProposalStatus.Rejected;
        var reviewedAt = DateTimeOffset.UtcNow;

        await using var conn = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        // Conditional UPDATE: only flip if still Pending — protects against concurrent decisions.
        cmd.CommandText = """
            UPDATE vais_recipe_proposals
               SET status = $1, reviewed_at = $2, reviewer_id = $3
             WHERE proposal_id = $4 AND status = 'Pending'
            """;
        cmd.Parameters.AddWithValue(nextStatus.ToString());
        cmd.Parameters.AddWithValue(reviewedAt);
        cmd.Parameters.AddWithValue(decidedBy);
        cmd.Parameters.AddWithValue(proposalId);
        var affected = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        // Either we updated or someone else beat us — re-read to return the truth-of-record.
        return await GetAsync(proposalId, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return conn;
    }

    private const string SelectColumns = """
        SELECT proposal_id, kind, concept, body, support, confidence, source_trace_ids,
               risk_level, status, created_at, reviewed_at, reviewer_id, name
        """;

    private static RecipeProposal Read(NpgsqlDataReader r) =>
        new()
        {
            ProposalId = r.GetString(0),
            Kind = Enum.Parse<RecipeProposalKind>(r.GetString(1), ignoreCase: true),
            Concept = r.GetString(2),
            Body = r.GetString(3),
            Support = r.GetInt32(4),
            Confidence = r.GetDouble(5),
            SourceTraceIds = JsonSerializer.Deserialize<List<string>>(r.GetString(6)) ?? new(),
            RiskLevel = Enum.Parse<RecipeProposalRiskLevel>(r.GetString(7), ignoreCase: true),
            Status = Enum.Parse<RecipeProposalStatus>(r.GetString(8), ignoreCase: true),
            CreatedAt = r.GetFieldValue<DateTimeOffset>(9),
            ReviewedAt = r.IsDBNull(10) ? null : r.GetFieldValue<DateTimeOffset>(10),
            ReviewerId = r.IsDBNull(11) ? null : r.GetString(11),
            Name = r.IsDBNull(12) ? null : r.GetString(12),
        };
}
