// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Threading.RateLimiting;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// A token bucket per linked device key on creating walls, separate from the general write bucket: at most
/// <see cref="DefaultBurst"/> walls at once, refilled one every <see cref="DefaultRefill"/> (five an hour). A runaway or
/// stolen key cannot fill an account with walls; the per-user cap on owned walls (<c>WallCreationOptions</c>) bounds the
/// total.
/// </summary>
/// <param name="burst">The bucket's size.</param>
/// <param name="refill">How often one token comes back.</param>
public sealed class DeviceKeyWallCreateRateLimiter(int burst = DeviceKeyWallCreateRateLimiter.DefaultBurst, TimeSpan? refill = null) : IDisposable
{
    public const int DefaultBurst = 5;

    /// <summary>One wall back every twelve minutes: five an hour.</summary>
    public static readonly TimeSpan DefaultRefill = TimeSpan.FromMinutes(12);

    private readonly TimeSpan period = refill ?? DefaultRefill;

    private readonly PartitionedRateLimiter<Guid> limiter = PartitionedRateLimiter.Create<Guid, Guid>(
        keyId => RateLimitPartition.GetTokenBucketLimiter(
            keyId,
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = burst,
                TokensPerPeriod = 1,
                ReplenishmentPeriod = refill ?? DefaultRefill,
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

    /// <summary>Seconds a refused caller should wait before the next token.</summary>
    public int RetryAfterSeconds => (int)Math.Ceiling(period.TotalSeconds);

    /// <summary>Takes one wall from <paramref name="keyId"/>'s bucket; false when it is empty.</summary>
    public bool TryAcquire(Guid keyId)
    {
        using var lease = limiter.AttemptAcquire(keyId);
        return lease.IsAcquired;
    }

    public void Dispose() => limiter.Dispose();
}
