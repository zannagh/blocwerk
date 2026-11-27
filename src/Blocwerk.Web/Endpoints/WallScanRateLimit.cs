// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Claims;
using System.Threading.RateLimiting;
using Blocwerk.Authentication.Endpoints;

namespace Blocwerk.Web.Endpoints;

/// <summary>
/// The scan upload's rate limit, per caller (the signed-in user or key owner, else the address): a package is up to a
/// few thousand files, sent a few at a time by a background session, so the bucket lets a whole package through in
/// minutes while a runaway client is held to about 20 requests a second.
/// </summary>
public static class WallScanRateLimit
{
    public const string Policy = "scans";

    private const int Burst = 1000;

    public static void AddWallScanRateLimit(this IServiceCollection services) =>
        services.AddRateLimitPolicy(
            Policy,
            http => RateLimitPartition.GetTokenBucketLimiter(
                http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = Burst,
                    TokensPerPeriod = 200,
                    ReplenishmentPeriod = TimeSpan.FromSeconds(10),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }),
            (context, _) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = "10";
                return ValueTask.CompletedTask;
            });
}
