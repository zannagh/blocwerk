// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Endpoints.Feed;

/// <summary>The feed stream's knobs; the defaults are production's, tests shorten them.</summary>
public sealed class FeedStreamOptions
{
    /// <summary>
    /// Gets how long one stream lives before the server says goodbye and the client reconnects (re-checking access), give
    /// or take <see cref="Jitter"/>.
    /// </summary>
    public TimeSpan Lifetime { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets the spread of <see cref="Lifetime"/> and <see cref="Retry"/> (0.2 = ±20 %), so streams opened together (after a
    /// deploy, or a gym's Wi-Fi coming back) do not all reconnect in the same second.
    /// </summary>
    public double Jitter { get; init; } = 0.2;

    /// <summary>Gets the gap after which an idle stream sends a comment, so proxies and phones keep it open.</summary>
    public TimeSpan Heartbeat { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets the browser's reconnect delay (<c>retry:</c>), give or take <see cref="Jitter"/>.</summary>
    public TimeSpan Retry { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Gets the catch-up page size on connect.</summary>
    public int PageSize { get; init; } = 500;

    /// <summary>Gets the catch-up pages a connect replays; a longer backlog is answered with a resync (reload is cheaper).</summary>
    public int MaxCatchUpPages { get; init; } = 4;

    /// <summary>Gets the hub deliveries buffered for a slow client before its stream is closed with <c>overflow</c>.</summary>
    public int QueueCapacity { get; init; } = 64;

    /// <summary>Gets the open streams of the whole process.</summary>
    public int MaxStreams { get; init; } = 2000;

    /// <summary>Gets the open streams of one signed-in user (tabs and devices together).</summary>
    public int MaxStreamsPerUser { get; init; } = 8;

    /// <summary>Gets the open anonymous streams (share link, kiosk) of one address partition (IPv6: one /64).</summary>
    public int MaxAnonymousPerAddress { get; init; } = 16;

    /// <summary>Gets the open anonymous streams of one wall.</summary>
    public int MaxAnonymousPerWall { get; init; } = 50;

    /// <summary>A value spread by <see cref="Jitter"/> around <paramref name="value"/>.</summary>
    public TimeSpan Spread(TimeSpan value) => value * (1 + (Jitter * ((Random.Shared.NextDouble() * 2) - 1)));
}
