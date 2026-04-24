# Streaming Journal Replay Implementation State

**Session Date**: 2026-04-23
**Feature**: v0.21 Streaming Journal Replay (deferred backlog task)

## Summary

Implementing streaming journal replay feature that enables delta-by-delta replay of streaming runs, not just tool-call replay. The feature adds `ReplayMode.Full` as an opt-in mode that journals each `CompletionUpdate` and replays them verbatim on resume, bypassing the provider.

## Implementation Status

### Completed

1. **ReplayMode enum** - `src/Vais.Agents.Abstractions/ReplayMode.cs`
   - ToolOnly = 0 (default, backward compatible)
   - Full = 1 (new opt-in for delta replay)

2. **CompletionDeltaRecorded journal entry** - `src/Vais.Agents.Abstractions/JournalEntry.cs`
   - Added to JournalEntry hierarchy
   - Properties: RunId, SequenceNumber, Delta, At

3. **StatefulAgentOptions** - `src/Vais.Agents.Core/StatefulAgentOptions.cs`
   - Added `ReplayMode` property with ToolOnly default

4. **PublicAPI updates** - `src/Vais.Agents.Abstractions/PublicAPI.Unshipped.txt`
   - ReplayMode enum entries
   - CompletionDeltaRecorded API entries
   - IA2AGraphNodeInvoker (unrelated - from A2A work)

5. **StatefulAiAgent streaming loop** - `src/Vais.Agents.Core/StatefulAiAgent.cs`
   - Added replay detection before Phase 1
   - Added delta journaling after yielding in Phase 2
   - Added `skipProvider` flag to bypass provider on replay
   - Added `skipToolDispatch` flag to skip tool calls on replay
   - Added tool outcome replay from journal

6. **Orleans serialization** - `src/Vais.Agents.Hosting.Orleans/JournalEntrySurrogate.cs`
   - Extended JournalEntryKind enum with CompletionDeltaRecorded = 1
   - Added fields: SequenceNumber, TextDelta, ModelId, PromptTokens, CompletionTokens, ToolCallsJson
   - Updated FromSurrogate/ToSurrogate
   - Added CompletionDeltaRecordedSurrogateConverter
   - Added ParseToolCalls/SerializeToolCalls helpers

7. **Test file** - `tests/Vais.Agents.Core.Tests/StreamingJournalReplayTests.cs`
   - 8 test cases created

8. **PublicAPI updates** - `src/Vais.Agents.Hosting.Orleans/PublicAPI.Unshipped.txt`
   - Orleans serialization API entries

### Build Errors (Remaining)

```
G:\work\vais_oss\agentic\src\Vais.Agents.Core\StatefulAiAgent.cs(603,37): error CS1503: Argument 7: cannot convert from 'string' to 'System.TimeSpan'
G:\work\vais_oss\agentic\src\Vais.Agents.Core\StatefulAiAgent.cs(930,25): error CS1739: The best overload for 'ChatTurn' does not have a parameter named 'ToolName'
G:\work\vais_oss\agentic\src\Vais.Agents.Core\StatefulAiAgent.cs(921,64): error CS8604: Possible null reference argument for parameter 'runId'
```

### Error Details

#### Error 1: CS1503 on line 603
ToolCallCompleted constructor signature issue - likely passing wrong argument type for duration/TimeSpan.

#### Error 2: CS1739 on line 930
ChatTurn constructor doesn't have `ToolName` parameter. Need to check ChatTurn signature and use correct parameters.

#### Error 3: CS8604 on line 921
`context.RunId` may be null but passed to `ReadAsync` which requires non-null string. Need null check.

## Current Code State

### Key Implementation in StatefulAiAgent.cs (lines 505-581)

```csharp
turnAccumulator.Clear();
IReadOnlyList<ToolCallRequest>? turnToolCalls = null;
string? turnModelId = null;
int? turnPromptTokens = null;
int? turnCompletionTokens = null;
var skipProvider = false;
var skipToolDispatch = false;

// Full replay mode check
if (_replayMode == ReplayMode.Full && context.RunId is not null && _journal is not NullAgentJournal)
{
    var replayEntries = new List<JournalEntry>();
    await foreach (var entry in _journal.ReadAsync(context.RunId, cancellationToken))
    {
        replayEntries.Add(entry);
    }

    var deltasToReplay = replayEntries
        .OfType<CompletionDeltaRecorded>()
        .Where(e => e.SequenceNumber >= deltaSequence)
        .OrderBy(e => e.SequenceNumber)
        .ToList();

    if (deltasToReplay.Count > 0)
    {
        skipProvider = true;
        // Replay deltas...
        // Replay tool outcomes...
    }
}
```

### Tool Dispatch Skip Logic (lines 909-934)

```csharp
workingHistory.Add(new ChatTurn(
    AgentChatRole.Assistant,
    turnAccumulator.ToString(),
    ToolCalls: turnToolCalls));

// Skip tool dispatch if we already replayed tool outcomes from journal
if (skipToolDispatch)
{
    // Append tool outcomes from journal to working history
    var toolOutcomes = new List<JournalEntry>();
    await foreach (var entry in _journal.ReadAsync(context.RunId, cancellationToken))  // LINE 921 - ERROR HERE
    {
        toolOutcomes.Add(entry);
    }
    foreach (var outcome in toolOutcomes.OfType<ToolCallRecorded>())
    {
        workingHistory.Add(new ChatTurn(
            AgentChatRole.Tool,
            outcome.Outcome.Result,
            ToolName: outcome.ToolName,  // LINE 930 - ERROR HERE
            ToolCallId: outcome.CallId));
    }
    continue;
}
```

### ToolCallCompleted Replay (around line 603)

```csharp
yield return new ToolCallCompleted(
    outcome.Outcome.Error is null ? outcome.At : outcome.At.Add(TimeSpan.FromTicks(1)),
    eventContext,
    outcome.Outcome.CallId,
    outcome.ToolName,
    outcome.Outcome.Error is null ? true : false,
    outcome.Outcome.Error,
    outcome.Outcome.Result);  // LINE 603 - ERROR HERE
```

## Test File State

`tests/Vais.Agents.Core.Tests/StreamingJournalReplayTests.cs` - 8 tests created:

1. `ToolOnlyMode_DoesNotJournalDeltas`
2. `FullMode_JournalsAllDeltas`
3. `FullMode_JournalsToolCallsOnDelta`
4. `Replay_ReYieldsExactDeltaSequence`
5. `Replay_BypassesProvider`
6. `Replay_WithToolCalls_ReplaysDeltasAndToolOutcomes`
7. `ToolOnlyMode_OnResume_ProviderReinvoked`
8. `SequenceNumbers_IncrementCorrectlyAcrossTurns`

## Next Steps

1. **Fix Build Errors**:
   - Line 603: Check ToolCallCompleted constructor signature and fix argument types
   - Line 921: Add null check for `context.RunId` before calling ReadAsync
   - Line 930: Check ChatTurn constructor signature and fix parameters (likely just ToolCallId, not ToolName)

2. **Run Tests**:
   - Execute `dotnet test --filter "FullyQualifiedName~StreamingJournalReplayTests"`
   - Verify all 8 tests pass

3. **Update Documentation**:
   - Update `docs/roadmap/deferred-backlog.md` to mark feature as shipped in v0.21

4. **Verify All Projects Build**:
   - Run `dotnet build --no-incremental` with 0 errors, 0 warnings

## Files Modified

### New Files
- `src/Vais.Agents.Abstractions/ReplayMode.cs`
- `tests/Vais.Agents.Core.Tests/StreamingJournalReplayTests.cs`

### Modified Files
- `src/Vais.Agents.Abstractions/JournalEntry.cs`
- `src/Vais.Agents.Abstractions/PublicAPI.Unshipped.txt`
- `src/Vais.Agents.Core/StatefulAgentOptions.cs`
- `src/Vais.Agents.Core/StatefulAiAgent.cs`
- `src/Vais.Agents.Hosting.Orleans/JournalEntrySurrogate.cs`
- `src/Vais.Agents.Hosting.Orleans/PublicAPI.Unshipped.txt`
- `src/Vais.Agents.Core/PublicAPI.Unshipped.txt` (from earlier work)

## Design Decisions

1. **Opt-in via ReplayMode**: Full delta replay is opt-in (ToolOnly default) to maintain backward compatibility and avoid unnecessary journal storage.

2. **Sequence Numbers**: Added to enable future per-delta resume (v0.21 replays from beginning, but structure supports mid-stream resume).

3. **Non-blocking Journal Failures**: Journal failures are logged but don't break the stream.

4. **Provider Bypass on Full Replay**: When ReplayMode.Full is enabled and journaled deltas exist, provider is completely bypassed.

5. **Tool Outcome Replay**: When deltas with tool calls are replayed, tool outcomes are also replayed from the journal to avoid re-invoking tools.

## Notes

- The implementation follows the existing JournalEntrySurrogate pattern for Orleans serialization.
- Tests use FakeStreamingProvider, FakeTool, and FakeRegistry for isolation.
- The `skipToolDispatch` flag ensures tools aren't re-invoked when replaying from journal.
- Build is currently failing due to constructor signature mismatches that need to be resolved.
