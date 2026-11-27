// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// The scan upload's rate limit (<see cref="WallScanRateLimiter"/>): an endpoint filter rather than a policy of the
/// rate-limiter middleware, because that middleware runs before authentication and could only count per address. The
/// filter runs after authorization, so it counts per user and per key.
/// </summary>
public static class WallScanRateLimit
{
    public static IServiceCollection AddWallScanRateLimit(this IServiceCollection services) =>
        services.AddSingleton(_ => new WallScanRateLimiter());

    /// <summary>Limits every route of <paramref name="group"/> with the one shared <see cref="WallScanRateLimiter"/>.</summary>
    public static RouteGroupBuilder RequireWallScanRateLimit(this RouteGroupBuilder group)
    {
        group.AddEndpointFilter((context, next) =>
            context.HttpContext.RequestServices.GetRequiredService<WallScanRateLimiter>().InvokeAsync(context, next));
        return group;
    }
}
