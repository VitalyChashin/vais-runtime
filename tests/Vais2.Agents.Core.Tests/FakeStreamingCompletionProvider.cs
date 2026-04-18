// Copyright (c) 2026 VAIS2 Platform contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;

namespace Vais2.Agents.Core.Tests;

/// <summary>
/// Test double that implements <see cref="ICompletionProvider"/> and
/// <see cref="IStreamingCompletionProvider"/> on the same class — mirrors the
/// real SK / MAF adapter shape. The stream yields a caller-specified sequence
/// of <see cref="CompletionUpdate"/>s in order.
/// </summary>
internal sealed class FakeStreamingCompletionProvider : ICompletionProvider, IStreamingCompletionProvider
{
    private readonly Func<CompletionRequest, IEnumerable<CompletionUpdate>> _stream;

    public FakeStreamingCompletionProvider(IEnumerable<CompletionUpdate> updates)
        : this(_ => updates)
    {
    }

    public FakeStreamingCompletionProvider(Func<CompletionRequest, IEnumerable<CompletionUpdate>> stream)
    {
        _stream = stream;
    }

    public List<CompletionRequest> Received { get; } = new();

    public string ProviderName => "FakeStreaming";

    public Task<CompletionResponse> CompleteAsync(CompletionRequest request, CancellationToken cancellationToken = default)
    {
        // Non-streaming path not exercised by streaming tests; drain the stream synchronously
        // so the provider is still a valid ICompletionProvider should a test need both paths.
        Received.Add(request);
        var joined = string.Concat(_stream(request).Select(u => u.TextDelta));
        return Task.FromResult(new CompletionResponse(joined, "fake-stream-model"));
    }

#pragma warning disable CS1998 // Async method lacks 'await' — iterator is synchronous by design.
    public async IAsyncEnumerable<CompletionUpdate> StreamAsync(
        CompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Received.Add(request);
        foreach (var update in _stream(request))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return update;
        }
    }
#pragma warning restore CS1998
}
