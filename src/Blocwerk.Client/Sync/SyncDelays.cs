// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Client.Sync;

/// <summary>
/// How long the sync loop waits before it asks again (plan §1, sync loop step 3). Every wait is spread by a jitter
/// in [0, 1), so a gym's phones behind one NAT address never spend the feed limiter's burst (120) together.
/// </summary>
public static class SyncDelays
{
    /// <summary>The limiter's own <c>Retry-After</c>, used when a 429 carries none.</summary>
    public const double DefaultRetryAfterSeconds = 5;

    private static readonly TimeSpan[] NetworkSteps = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(120)];

    /// <summary>The first retry after the browser comes back <c>online</c> waits up to this long.</summary>
    public static TimeSpan OnlineSpread { get; } = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan[] RefusedSteps = [TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(300)];

    /// <summary>Gets the spread of the probe after a <c>bye</c>: a shutdown says bye to every client at once.</summary>
    public static TimeSpan ByeSpread { get; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The n-th network failure in a row (1-based): 5 s, 30 s, then 120 s, each ±50 % (wider than the plan's ±20 %, so
    /// the clients a deploy's outage stopped together do not come back together).
    /// </summary>
    /// <param name="failures">The failures so far, this one included.</param>
    /// <param name="jitter">A value in [0, 1).</param>
    /// <returns>The wait.</returns>
    public static TimeSpan Network(int failures, double jitter) =>
        NetworkSteps[Math.Clamp(failures, 1, NetworkSteps.Length) - 1] * (0.5 + jitter);

    /// <summary>
    /// The n-th stream in a row that failed before it delivered anything while <c>/changes</c> answered (1-based): a
    /// stream cap's 429 (<c>Retry-After: 30</c>), which an <c>EventSource</c> hides, or a proxy that blocks SSE. 30 s,
    /// 60 s, 120 s, then 5 min, each plus 0–20 %, so a capped phone never reconnects in a loop.
    /// </summary>
    /// <param name="failures">The quick failures so far, this one included.</param>
    /// <param name="jitter">A value in [0, 1).</param>
    /// <returns>The wait before the stream is opened again.</returns>
    public static TimeSpan StreamRefused(int failures, double jitter) =>
        RefusedSteps[Math.Clamp(failures, 1, RefusedSteps.Length) - 1] * (1 + (0.2 * jitter));

    /// <summary>The wait before the probe after a <c>bye</c>: a random 0–3 s.</summary>
    /// <param name="jitter">A value in [0, 1).</param>
    /// <returns>The wait.</returns>
    public static TimeSpan Bye(double jitter) => ByeSpread * jitter;

    /// <summary>A 429: the server's <c>Retry-After</c> (5 s from the limiter, 30 s from the stream caps) plus 0–20 %.</summary>
    /// <param name="retryAfterSeconds">The header's seconds, or null.</param>
    /// <param name="jitter">A value in [0, 1).</param>
    /// <returns>The wait.</returns>
    public static TimeSpan RetryAfter(double? retryAfterSeconds, double jitter) =>
        TimeSpan.FromSeconds((retryAfterSeconds is > 0 ? retryAfterSeconds.Value : DefaultRetryAfterSeconds) * (1 + (0.2 * jitter)));

    /// <summary>The first retry after <c>online</c>: a random 0–10 s.</summary>
    /// <param name="jitter">A value in [0, 1).</param>
    /// <returns>The wait.</returns>
    public static TimeSpan Online(double jitter) => OnlineSpread * jitter;
}
