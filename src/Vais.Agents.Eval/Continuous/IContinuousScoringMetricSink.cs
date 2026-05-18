// Copyright (c) 2026 VAIS contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

namespace Vais.Agents.Eval.Continuous;

/// <summary>
/// Sink for continuous-scoring Prometheus metrics. No-op default; replaced by
/// <c>PrometheusContinuousScoringMetricSink</c> when Prometheus is wired.
/// </summary>
public interface IContinuousScoringMetricSink
{
    /// <summary>Record the outcome of one scored production sample.</summary>
    void RecordSample(string suiteId, string status);

    /// <summary>Observe an assertion score for one assertion on a scored sample.</summary>
    void ObserveAssertionScore(string suiteId, string kind, double score);

    /// <summary>Update the current window's sampled count gauge.</summary>
    void SetWindowSampledCount(string suiteId, double count);

    /// <summary>Update the gauge for seconds remaining in the current window.</summary>
    void SetWindowSecondsRemaining(string suiteId, double seconds);
}

/// <summary>No-op metric sink used when Prometheus is not wired.</summary>
public sealed class NoopContinuousScoringMetricSink : IContinuousScoringMetricSink
{
    /// <inheritdoc/>
    public void RecordSample(string suiteId, string status) { }
    /// <inheritdoc/>
    public void ObserveAssertionScore(string suiteId, string kind, double score) { }
    /// <inheritdoc/>
    public void SetWindowSampledCount(string suiteId, double count) { }
    /// <inheritdoc/>
    public void SetWindowSecondsRemaining(string suiteId, double seconds) { }
}
