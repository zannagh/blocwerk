// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Claims;
using System.Threading.RateLimiting;
using Blocwerk.Authentication.Authorization;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// A token bucket per caller of the scan upload: the user together with the personal key (or the browser session) they
/// call with, so two keys of one user, or two people behind one address, never share a bucket. A package is up to
/// <see cref="Core.Capture.Scan.ScanPackageLimits.MaxFiles"/> files, which the app sends about 20 at a time and retries;
/// small depth and confidence images can go through at a few hundred a second on a good connection. The bucket lets a
/// burst of <see cref="DefaultBurst"/> and <see cref="DefaultPerSecond"/> a second through, so such a phone never sees a
/// 429, while a runaway client is held there. Beyond it: 429 with <c>Retry-After: 1</c>.
/// </summary>
public sealed class WallScanRateLimiter(int burst = WallScanRateLimiter.DefaultBurst, int perSecond = WallScanRateLimiter.DefaultPerSecond)
    : IEndpointFilter, IDisposable
{
    public const int DefaultBurst = 4000;

    public const int DefaultPerSecond = 200;

    private readonly PartitionedRateLimiter<HttpContext> limiter = PartitionedRateLimiter.Create<HttpContext, string>(
        http => RateLimitPartition.GetTokenBucketLimiter(
            PartitionKey(http),
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = burst,
                TokensPerPeriod = perSecond,
                ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

    /// <summary>The bucket's key: user and key id once authenticated, else (never on these routes) the address.</summary>
    public static string PartitionKey(HttpContext http)
    {
        var user = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
        return user is null
            ? $"address:{http.Connection.RemoteIpAddress}"
            : $"user:{user}|key:{http.User.FindFirstValue(ApiKeyClaimTypes.ApiKeyId) ?? "session"}";
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        using var lease = limiter.AttemptAcquire(context.HttpContext);
        if (!lease.IsAcquired)
        {
            context.HttpContext.Response.Headers.RetryAfter = "1";
            return Results.Json(
                new ScanErrorResponse("Too many requests; wait a second and send again.", []), statusCode: StatusCodes.Status429TooManyRequests);
        }

        return await next(context);
    }

    public void Dispose() => limiter.Dispose();
}
