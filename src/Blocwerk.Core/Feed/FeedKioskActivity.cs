// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Feed;

/// <summary>
/// The delivery-time rule that lets a kiosk tablet read its wall's activity live without widening the rows' stored audience:
/// <c>activity.added</c> is a <see cref="FeedAudience.Members"/> row, and a share link must never get it. A viewer receives
/// such a row only for a kind it names in <see cref="FeedViewer.MemberKinds"/> (the kiosk's) and only when the entry passes
/// <see cref="KioskActivityVisibility"/> now (<see cref="FeedKioskFacts.Readable"/>), so the live list carries exactly
/// what the log read shows the tablet. An <c>activity.removed</c> row has no entry left to check, so its boulder decides
/// (<see cref="FeedKioskFacts.OpenBoulders"/>). Every other row goes by its audience alone (<see cref="FeedViewer.Allows"/>).
/// </summary>
internal static class FeedKioskActivity
{
    /// <summary>True when <paramref name="row"/> may reach <paramref name="viewer"/>, its audiences or this rule allowing.</summary>
    public static bool Allows(FeedViewer viewer, WallChange row, FeedHydrated hydrated) =>
        viewer.Allows(row)
        || (row.Audience == FeedAudience.Members
            && viewer.MemberKinds.Contains(row.Kind)
            && (row.Kind == FeedKinds.ActivityRemoved
                ? row.BoulderId is { } boulderId && hydrated.Kiosk.OpenBoulders.Contains(boulderId)
                : row.EntityId is { } entryId && hydrated.Kiosk.Readable.Contains(entryId)));
}
