// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Threading.RateLimiting;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// A token bucket per linked device key on the companion app's writes (marks, comments, profile, wall links), shared by
/// all of them and counted by the key's id, not by address: phones behind one gym NAT never share a bucket, and a
/// runaway or stolen key cannot spend anyone else's.
/// </summary>
/// <remarks>
/// Sized for normal logging: <see cref="DefaultBurst"/> writes at once (an offline queue draining a session's ticks,
/// marks and comments), then <see cref="DefaultPerSecond"/> a second (60 a minute). Beyond it: 429 with
/// <c>Retry-After: 1</c>, which the app's queue retries.
/// </remarks>
/// <param name="burst">The bucket's size.</param>
/// <param name="perPeriod">The writes it refills per <paramref name="period"/>.</param>
/// <param name="period">The refill period; one second unless a test needs a bucket that does not refill meanwhile.</param>
public sealed class DeviceKeyWriteRateLimiter(
    int burst = DeviceKeyWriteRateLimiter.DefaultBurst,
    int perPeriod = DeviceKeyWriteRateLimiter.DefaultPerSecond,
    TimeSpan? period = null) : IDisposable
{
    public const int DefaultBurst = 60;

    public const int DefaultPerSecond = 1;

    private readonly PartitionedRateLimiter<Guid> limiter = PartitionedRateLimiter.Create<Guid, Guid>(
        keyId => RateLimitPartition.GetTokenBucketLimiter(
            keyId,
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = burst,
                TokensPerPeriod = perPeriod,
                ReplenishmentPeriod = period ?? TimeSpan.FromSeconds(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

    /// <summary>Takes one write from <paramref name="keyId"/>'s bucket; false when it is empty.</summary>
    public bool TryAcquire(Guid keyId)
    {
        using var lease = limiter.AttemptAcquire(keyId);
        return lease.IsAcquired;
    }

    public void Dispose() => limiter.Dispose();
}
