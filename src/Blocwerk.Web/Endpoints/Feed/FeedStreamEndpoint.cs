// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Microsoft.AspNetCore.Mvc;

namespace Blocwerk.Web.Endpoints.Feed;

/// <summary>
/// <c>GET /api/walls/{wallId}/feed/stream?after=&amp;token=</c>: the wall's events as server-sent events
/// (<see cref="Blocwerk.Contracts.Feed.FeedStreamEvents"/>). The cursor is <c>Last-Event-ID</c> when the browser
/// reconnects, else <c>?after=</c> (an <c>EventSource</c> cannot set headers on its first request); without either the
/// stream opens with a resync. Refused like the catch-up (401/403/410), and 429 with <c>Retry-After</c> when the
/// process, the user, the anonymous viewers of the address or of the wall hold too many streams.
/// </summary>
internal static class FeedStreamEndpoint
{
    public const string LastEventIdHeader = "Last-Event-ID";

    private const string BusyRetryAfterSeconds = "30";

    internal static async Task<IResult> HandleAsync(
        Guid wallId,
        [FromQuery] string? after,
        [FromQuery] string? token,
        HttpContext http,
        [FromServices] FeedStreamHost host,
        CancellationToken ct)
    {
        var access = await FeedRequestAccess.ForWallAsync(http, wallId, token, ct);
        if (access.Viewer is not { } viewer)
        {
            return FeedRequestAccess.Refused(http, access.Refusal);
        }

        using var lease = host.Limits.TryAcquire(
            new FeedStreamClaim(access.CallerUserId, wallId, FeedAddress.PartitionOf(http.Connection.RemoteIpAddress)));
        if (lease is null)
        {
            FeedRequestAccess.NoStore(http);
            http.Response.Headers.RetryAfter = BusyRetryAfterSeconds;
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }

        var lastEventId = http.Request.Headers[LastEventIdHeader].ToString();
        var request = new FeedStreamRequest(
            wallId,
            viewer,
            string.IsNullOrEmpty(lastEventId) ? after : lastEventId,
            async c => (await FeedRequestAccess.ForWallAsync(http, wallId, token, c)).Viewer);
        try
        {
            await new FeedStreamSession(host, new FeedStreamWriter(http.Response)).RunAsync(request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The client went away; nothing to answer.
        }

        return Results.Empty;
    }
}
