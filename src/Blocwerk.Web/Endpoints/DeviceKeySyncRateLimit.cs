// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// The device keys' rate limit on the sync reads (<see cref="DeviceKeySyncRateLimiter"/>): an endpoint filter, like
/// <see cref="WallScanRateLimit"/>, because the rate-limiter middleware runs before authentication and could only count
/// per address. The filter runs after authorization, so it counts per key.
/// </summary>
public static class DeviceKeySyncRateLimit
{
    public static IServiceCollection AddDeviceKeySyncRateLimit(this IServiceCollection services) =>
        services.AddSingleton(_ => new DeviceKeySyncRateLimiter());

    /// <summary>Counts a device key's calls of <paramref name="route"/> in its one shared bucket.</summary>
    public static RouteHandlerBuilder RequireDeviceKeySyncRateLimit(this RouteHandlerBuilder route) =>
        route.AddEndpointFilter((context, next) =>
            context.HttpContext.RequestServices.GetRequiredService<DeviceKeySyncRateLimiter>().InvokeAsync(context, next));
}
