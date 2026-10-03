// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Threading.RateLimiting;

namespace Blocwerk.Core.Runners;

/// <summary>
/// The per-runner half of the runner API's rate limit, applied AFTER the key authenticated (the HTTP policy in front of
/// it can only partition by address, since anything taken from an unauthenticated header is the caller's to rotate).
/// Generous for a well-behaved runner: a claim every 25 s and a progress report every few seconds.
/// </summary>
public sealed class RunnerCallLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<Guid> limiter = PartitionedRateLimiter.Create<Guid, Guid>(
        runnerId => RateLimitPartition.GetTokenBucketLimiter(
            runnerId,
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 60,
                TokensPerPeriod = 20,
                ReplenishmentPeriod = TimeSpan.FromSeconds(10),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

    /// <summary>Whether <paramref name="runnerId"/> may make one more call now.</summary>
    public bool TryAcquire(Guid runnerId)
    {
        using var lease = limiter.AttemptAcquire(runnerId);
        return lease.IsAcquired;
    }

    public void Dispose() => limiter.Dispose();
}
