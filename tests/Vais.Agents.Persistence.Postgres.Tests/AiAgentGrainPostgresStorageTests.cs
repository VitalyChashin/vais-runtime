// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using FluentAssertions;
using Npgsql;
using Vais.Agents.Hosting.Orleans;
using Xunit;

namespace Vais.Agents.Persistence.Postgres.Tests;

/// <summary>
/// End-to-end tests that prove <see cref="AiAgentGrain"/> stores and restores state
/// through the Postgres ADO.NET grain-storage provider wired by
/// <see cref="AgenticPostgresPersistenceExtensions.AddAgenticPostgresGrainStorage(ISiloBuilder, string)"/>.
/// </summary>
[Collection(PostgresClusterCollection.CollectionName)]
public sealed class AiAgentGrainPostgresStorageTests
{
    private readonly PostgresClusterFixture _fx;

    public AiAgentGrainPostgresStorageTests(PostgresClusterFixture fx) => _fx = fx;

    [Fact]
    public async Task Ask_Writes_History_To_Postgres_And_Reads_It_Back()
    {
        var grain = _fx.Cluster.GrainFactory.GetGrain<IAiAgentGrain>("pg-ask-once");
        try
        {
            var reply = await grain.AskAsync("hello");

            reply.Should().Be("history-size=1");
            var history = await grain.GetHistoryAsync();
            history.Should().HaveCount(2);
            history[0].Text.Should().Be("hello");
            history[1].Text.Should().Be("history-size=1");
        }
        finally
        {
            await grain.DeleteAsync();
        }
    }

    [Fact]
    public async Task History_Persists_Across_Activation_Collection_Backed_By_Postgres()
    {
        var grainId = "pg-persist";
        var grain = _fx.Cluster.GrainFactory.GetGrain<IAiAgentGrain>(grainId);
        try
        {
            await grain.AskAsync("turn-1");
            await grain.AskAsync("turn-2");
            (await grain.GetHistoryAsync()).Should().HaveCount(4);

            var mgmt = _fx.Cluster.GrainFactory.GetGrain<IManagementGrain>(0);
            await mgmt.ForceActivationCollection(TimeSpan.Zero);

            var rehydrated = _fx.Cluster.GrainFactory.GetGrain<IAiAgentGrain>(grainId);
            var rehistory = await rehydrated.GetHistoryAsync();
            rehistory.Should().HaveCount(4);

            // Provider sees 5 = 4 prior + new user turn → Postgres rehydration worked.
            var reply = await rehydrated.AskAsync("turn-3");
            reply.Should().Be("history-size=5");
        }
        finally
        {
            await grain.DeleteAsync();
        }
    }

    [Fact]
    public async Task Grain_State_Appears_As_A_Row_In_OrleansStorage_Table()
    {
        var grainId = "pg-row-inspect";
        var grain = _fx.Cluster.GrainFactory.GetGrain<IAiAgentGrain>(grainId);
        try
        {
            await grain.AskAsync("hello");

            await using var conn = new NpgsqlConnection(_fx.ConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT COUNT(*) FROM OrleansStorage WHERE grainidextensionstring = @id",
                conn);
            cmd.Parameters.AddWithValue("id", grainId);
            var count = (long)(await cmd.ExecuteScalarAsync() ?? 0L);
            count.Should().BeGreaterThan(0, "grain state should have been persisted as a row in OrleansStorage");
        }
        finally
        {
            await grain.DeleteAsync();
        }
    }

    [Fact]
    public async Task Delete_Clears_State_So_Reactivation_Starts_Fresh_From_Postgres()
    {
        var grainId = "pg-delete";
        var grain = _fx.Cluster.GrainFactory.GetGrain<IAiAgentGrain>(grainId);

        await grain.AskAsync("turn-1");
        (await grain.GetHistoryAsync()).Should().HaveCount(2);

        await grain.DeleteAsync();

        var mgmt = _fx.Cluster.GrainFactory.GetGrain<IManagementGrain>(0);
        await mgmt.ForceActivationCollection(TimeSpan.Zero);

        var reborn = _fx.Cluster.GrainFactory.GetGrain<IAiAgentGrain>(grainId);
        (await reborn.GetHistoryAsync()).Should().BeEmpty(
            "ClearStateAsync (via grain.DeleteAsync) must leave the grain with no history");
        (await reborn.GetSystemPromptAsync()).Should().BeNull();

        await reborn.DeleteAsync();
    }

    [Fact]
    public async Task SystemPrompt_Persists_Via_Postgres_Across_Activation_Collection()
    {
        var grainId = "pg-prompt";
        var grain = _fx.Cluster.GrainFactory.GetGrain<IAiAgentGrain>(grainId);
        try
        {
            await grain.SetSystemPromptAsync("be-concise");

            var mgmt = _fx.Cluster.GrainFactory.GetGrain<IManagementGrain>(0);
            await mgmt.ForceActivationCollection(TimeSpan.Zero);

            var rehydrated = _fx.Cluster.GrainFactory.GetGrain<IAiAgentGrain>(grainId);
            (await rehydrated.GetSystemPromptAsync()).Should().Be("be-concise");
        }
        finally
        {
            await grain.DeleteAsync();
        }
    }
}
