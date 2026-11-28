// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Threading.RateLimiting;
using Blocwerk.Authentication.Endpoints;

namespace Blocwerk.Web.Endpoints;

/// <summary>The rate limit of the device link's code exchange: a code is 256 bits, but nobody gets to keep guessing.</summary>
public static class DeviceLinkRateLimit
{
    public const string Policy = "device-link-token";

    /// <summary>Exchanges allowed per client address per <see cref="Window"/>, successes included.</summary>
    public const int PermitsPerWindow = 10;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static IServiceCollection AddDeviceLinkRateLimit(this IServiceCollection services) =>
        services.AddRateLimitPolicy(
            Policy,
            http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = PermitsPerWindow,
                    Window = Window,
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }),
            (context, _) =>
            {
                context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger(typeof(DeviceLinkRateLimit))
                    .LogWarning(
                        "Device link token rate limit hit from {RemoteIp}",
                        context.HttpContext.Connection.RemoteIpAddress);
                return ValueTask.CompletedTask;
            });
}
