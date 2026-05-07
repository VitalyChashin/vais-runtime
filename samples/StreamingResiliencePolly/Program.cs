// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

// StreamingResiliencePolly — StatefulAgentOptions.StreamingResiliencePipeline with Polly retries.
//
// Run: dotnet run --project samples/StreamingResiliencePolly
// Env: none (deterministic, no API key)
// Docs: docs/concepts/resilience.md
//
// A FlakyStreamingProvider throws TransientException on the first 2 calls;
// the Polly ResiliencePipeline retries and the 3rd call succeeds.
//
// Key constraint: retries only fire before the first CompletionUpdate is yielded.
// Once the stream is producing deltas, the content is considered committed and
// the pipeline lets errors propagate (preventing partial re-delivery to the caller).

using System.Runtime.CompilerServices;
using Polly;
using Polly.Retry;
using Vais.Agents;
using Vais.Agents.Core;

var provider = new FlakyStreamingProvider();

var pipeline = new ResiliencePipelineBuilder()
    .AddRetry(new RetryStrategyOptions
    {
        MaxRetryAttempts    = 2,
        Delay               = TimeSpan.FromMilliseconds(50),
        BackoffType         = DelayBackoffType.Constant,
        ShouldHandle        = new PredicateBuilder().Handle<TransientException>(),
        OnRetry             = args =>
        {
            Console.WriteLine($"  [polly] retry #{args.AttemptNumber + 1}: {args.Outcome.Exception!.Message}");
            return default;
        },
    })
    .Build();

var agent = new StatefulAiAgent(provider, new StatefulAgentOptions
{
    StreamingResiliencePipeline = pipeline,
});

Console.WriteLine("Starting streaming turn...");
Console.Write("stream: ");
await foreach (var delta in agent.StreamAsync("Say something short."))
{
    Console.Write(delta);
}
Console.WriteLine();
Console.WriteLine();
Console.WriteLine($"Total provider calls: {provider.Attempts} (expected 3)");

// ---- Flaky streaming provider ----
// Throws TransientException on the first 2 calls; succeeds on the 3rd.
sealed class FlakyStreamingProvider : ICompletionProvider, IStreamingCompletionProvider
{
    public int Attempts { get; private set; }
    public string ProviderName => "flaky";

    public Task<CompletionResponse> CompleteAsync(CompletionRequest req, CancellationToken ct = default)
        => throw new NotSupportedException("streaming only");

#pragma warning disable CS1998
    public async IAsyncEnumerable<CompletionUpdate> StreamAsync(
        CompletionRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        Attempts++;
        // throw before yielding any delta so the resilience pipeline can retry
        if (Attempts <= 2)
            throw new TransientException($"transient failure on attempt {Attempts}");

        yield return new CompletionUpdate("Success ");
        yield return new CompletionUpdate($"on attempt {Attempts}.");
    }
#pragma warning restore CS1998
}

// ---- Transient exception type ----
sealed class TransientException(string message) : Exception(message);
