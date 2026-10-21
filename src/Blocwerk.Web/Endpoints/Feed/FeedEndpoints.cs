// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Globalization;
using System.Threading.RateLimiting;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Endpoints;
using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Feed;
using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Endpoints.Feed;

/// <summary>
/// The feed over HTTP (design §5.8, §7.2): the catch-up pages of one wall and of the caller's own rows, and the wall's
/// server-sent event stream (<see cref="FeedStreamEndpoint"/>).
/// </summary>
/// <remarks>
/// <para>
/// The wall routes take the wall media's posture: the gallery-image policy (a signed-in human, a share token, or a kiosk
/// tablet), API-key principals rejected, and the grant decided per request by <see cref="FeedAccessGate"/>. The rows are
/// then narrowed to that viewer's audiences, exactly as the hub narrows a circuit's deliveries. <c>/api/me/changes</c> is
/// outside the API-key surface, so a key presented there authenticates nobody.
/// </para>
/// <para>
/// All three are limited per address (<see cref="RateLimitPolicy"/>): a token bucket generous enough for a gym's phones
/// behind one NAT paging through a backlog, and tight enough that a loop hammering the horizon read is cut off.
/// </para>
/// </remarks>
public static class FeedEndpoints
{
    public const string WallChangesRoute = "/api/walls/{wallId:guid}/changes";

    public const string StreamRoute = "/api/walls/{wallId:guid}/feed/stream";

    public const string SelfChangesRoute = "/api/me/changes";

    public const string RateLimitPolicy = "feed";

    private const int AddressBurst = 120;

    private const int RetryAfterSeconds = 5;

    /// <summary>True for a request to one of the feed routes (the cookie challenge gives those a refusal body).</summary>
    public static bool IsFeedPath(PathString path)
    {
        if (path.StartsWithSegments(SelfChangesRoute, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var segments = (path.Value ?? string.Empty).ToLowerInvariant().Split('/', StringSplitOptions.RemoveEmptyEntries);
        return (segments is ["api", "walls", var wall, "changes"] && Guid.TryParse(wall, out _))
            || (segments is ["api", "walls", var streamWall, "feed", "stream"] && Guid.TryParse(streamWall, out _));
    }

    public static void MapFeed(this WebApplication app)
    {
        app.MapGet(WallChangesRoute, WallChangesAsync)
            .RequireAuthorization(BlocwerkPolicies.WallGalleryImage)
            .DenyApiKeyPrincipals()
            .RequireRateLimiting(RateLimitPolicy);
        app.MapGet(StreamRoute, FeedStreamEndpoint.HandleAsync)
            .RequireAuthorization(BlocwerkPolicies.WallGalleryImage)
            .DenyApiKeyPrincipals()
            .RequireRateLimiting(RateLimitPolicy)
            .WithMetadata(new LongPollEndpoint());
        app.MapGet(SelfChangesRoute, SelfChangesAsync)
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitPolicy);
    }

    /// <summary>
    /// Per address (an IPv6 caller per /64, <see cref="FeedAddress"/>): about two requests a second sustained, bursts up to
    /// <see cref="AddressBurst"/>.
    /// </summary>
    public static IServiceCollection AddFeedRateLimit(this IServiceCollection services) =>
        services.AddRateLimitPolicy(
            RateLimitPolicy,
            http => RateLimitPartition.GetTokenBucketLimiter(
                FeedAddress.PartitionOf(http.Connection.RemoteIpAddress),
                _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = AddressBurst,
                    TokensPerPeriod = 20,
                    ReplenishmentPeriod = TimeSpan.FromSeconds(10),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }),
            (context, _) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
                return ValueTask.CompletedTask;
            });

    /// <summary><c>?after={cursor}&amp;limit=1..1000&amp;token=</c>: one page of the wall's events the caller may see.</summary>
    internal static async Task<IResult> WallChangesAsync(
        Guid wallId,
        [FromQuery] string? after,
        [FromQuery] int? limit,
        [FromQuery] string? token,
        HttpContext http,
        [FromServices] FeedCatchUp catchUp,
        CancellationToken ct)
    {
        var access = await FeedRequestAccess.ForWallAsync(http, wallId, token, ct);
        if (access.Viewer is not { } viewer)
        {
            return FeedRequestAccess.Refused(http, access.Refusal);
        }

        FeedRequestAccess.NoStore(http);
        return Results.Json(await catchUp.ReadWallAsync(wallId, viewer, after, limit, ct), FeedJson.Options);
    }

    /// <summary><c>?after={cursor}&amp;limit=</c>: one page of the caller's own rows across walls.</summary>
    internal static async Task<IResult> SelfChangesAsync(
        [FromQuery] string? after,
        [FromQuery] int? limit,
        HttpContext http,
        [FromServices] FeedCatchUp catchUp,
        CancellationToken ct)
    {
        var access = await FeedRequestAccess.ForSelfAsync(http);
        if (access.Viewer is not { UserId: { } userId })
        {
            return FeedRequestAccess.Refused(http, access.Refusal);
        }

        FeedRequestAccess.NoStore(http);
        return Results.Json(await catchUp.ReadSelfAsync(userId, after, limit, ct), FeedJson.Options);
    }
}
