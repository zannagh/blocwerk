// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Contracts.Feed;

/// <summary>
/// One catch-up page (<c>GET /api/walls/{wallId}/changes</c>, <c>GET /api/me/changes</c>, design §5.8). Apply
/// <see cref="Events"/> by id, then continue with <c>?after=</c><see cref="Next"/>; while <see cref="More"/> is true, ask
/// again straight away.
/// </summary>
/// <param name="Events">The events after the cursor the caller may see, in feed order; empty on a resync.</param>
/// <param name="Next">The cursor to continue after. On a resync: reload, then continue from here.</param>
/// <param name="Resync">
/// True when the cursor was missing, unparseable, of another epoch or older than retention: events may be missing, so the
/// client reloads its documents instead of applying a gap.
/// </param>
/// <param name="More">True when the page was full: more events may follow right away.</param>
public sealed record FeedChanges(IReadOnlyList<FeedEnvelope> Events, string Next, bool Resync, bool More);
