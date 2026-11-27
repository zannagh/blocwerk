// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Claims;
using System.Threading.RateLimiting;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// One scan begin in flight per user (over all their keys and sessions): a begin holds its manifest, up to
/// <see cref="Core.Capture.Scan.ScanPackageLimits.MaxManifestBytes"/>, in memory while it is read and checked, so one
/// user cannot hold many of them at once. A second begin while one runs: 429 with <c>Retry-After: 1</c>. An endpoint
/// filter after authorization, like <see cref="WallScanRateLimiter"/>.
/// </summary>
public sealed class WallScanBeginGate : IEndpointFilter, IDisposable
{
    private readonly PartitionedRateLimiter<HttpContext> limiter = PartitionedRateLimiter.Create<HttpContext, string>(
        http => RateLimitPartition.GetConcurrencyLimiter(
            CallerKey(http),
            _ => new ConcurrencyLimiterOptions { PermitLimit = 1, QueueLimit = 0 }));

    /// <summary>The user, once authenticated, else (never on these routes) the address.</summary>
    public static string CallerKey(HttpContext http) =>
        http.User.FindFirstValue(ClaimTypes.NameIdentifier) is { } user ? $"user:{user}" : $"address:{http.Connection.RemoteIpAddress}";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        using var lease = limiter.AttemptAcquire(context.HttpContext);
        if (!lease.IsAcquired)
        {
            context.HttpContext.Response.Headers.RetryAfter = "1";
            return Results.Json(
                new ScanErrorResponse("Another scan begin of yours is still running; wait for it and send again.", []),
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        return await next(context);
    }

    public void Dispose() => limiter.Dispose();
}
