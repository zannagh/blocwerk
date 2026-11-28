// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Threading.RateLimiting;
using Blocwerk.Authentication.Authorization;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// A token bucket per linked device key on the offline client's sync reads (documents, <c>/a/</c> assets and the
/// manifest), shared by all of them. A cookie caller and every other principal pass untouched; other keys never reach
/// these handlers anyway.
/// </summary>
/// <remarks>
/// Sized for a full sync of a big wall: its manifest lists one entry per panel and wall photo, per facet texture and
/// mask, per splat level, per beta video thumbnail and per gallery image, which stays well under two thousand even on
/// a wall with hundreds of facets and videos. The bucket holds <see cref="DefaultBurst"/> requests, so such a sync
/// (plus its documents and the manifest itself) goes through at once, and refills <see cref="DefaultPerSecond"/> a
/// second (1,800 a minute) for the next one. A runaway or stolen key is held at that rate. Beyond it: a bare 429 with
/// <c>Retry-After: 1</c>.
/// </remarks>
/// <param name="burst">The bucket's size.</param>
/// <param name="perPeriod">The requests it refills per <paramref name="period"/>.</param>
/// <param name="period">The refill period; one second unless a test needs a bucket that does not refill meanwhile.</param>
public sealed class DeviceKeySyncRateLimiter(
    int burst = DeviceKeySyncRateLimiter.DefaultBurst,
    int perPeriod = DeviceKeySyncRateLimiter.DefaultPerSecond,
    TimeSpan? period = null) : IEndpointFilter, IDisposable
{
    public const int DefaultBurst = 3000;

    public const int DefaultPerSecond = 30;

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

    /// <summary>Takes one request from <paramref name="keyId"/>'s bucket; false when it is empty.</summary>
    public bool TryAcquire(Guid keyId)
    {
        using var lease = limiter.AttemptAcquire(keyId);
        return lease.IsAcquired;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var user = context.HttpContext.User;
        if (!user.IsDeviceKey() || user.GetApiKeyId() is not { } keyId)
        {
            return await next(context);
        }

        if (!TryAcquire(keyId))
        {
            context.HttpContext.Response.Headers.RetryAfter = "1";
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }

        return await next(context);
    }

    public void Dispose() => limiter.Dispose();
}
