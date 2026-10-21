// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Contracts.Feed;

/// <summary>
/// The server-sent events of <c>GET /api/walls/{wallId}/feed/stream</c> (design §7.2). Every event carries an <c>id:</c>
/// that is a feed cursor, so the browser's automatic reconnect sends the last one back as <c>Last-Event-ID</c> and the
/// stream resumes after it.
/// <list type="bullet">
/// <item><description>The default event (<c>message</c>): one <see cref="FeedEnvelope"/>.</description></item>
/// <item><description><see cref="Resync"/>: a <see cref="FeedChanges"/> with no events and <c>resync: true</c>. Reload the
/// wall's documents, then keep listening: its <c>id</c> is the cursor to continue from.</description></item>
/// <item><description><see cref="Bye"/>: a <see cref="FeedStreamBye"/>; the server closes the stream after it, and the
/// client reconnects with its last id (a refused reconnect answers 401/403/410 as the catch-up does).</description></item>
/// </list>
/// </summary>
public static class FeedStreamEvents
{
    public const string Resync = "resync";

    public const string Bye = "bye";

    /// <summary>The stream reached its lifetime, or the server is stopping; reconnecting re-checks access.</summary>
    public const string ByeLifetime = "lifetime";

    /// <summary>The caller's access changed (removed, demoted, promoted, the share link rotated); reconnect to find out how.</summary>
    public const string ByeAccess = "access";

    /// <summary>The client read too slowly and fell behind; reconnect and catch up from the last id.</summary>
    public const string ByeOverflow = "overflow";
}
