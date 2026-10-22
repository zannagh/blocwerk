// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Linq.Expressions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

/// <summary>
/// What a kiosk tablet may read of its wall's activity log: what members read, minus what a public screen has no business
/// showing. An entry about a boulder is shown only while that boulder is published and not archived (the tablet never
/// learns of a draft, and lists no archived boulder), and the membership entries (who joined, whose role changed) and the
/// photo-staging steps of a wall update in progress are left out. The log reads (<see cref="ActivityLogService"/>) and the
/// live feed's kiosk delivery (<c>FeedKioskActivity</c>) both apply this one rule, so the tablet's list and its live
/// updates never disagree.
/// </summary>
public static class KioskActivityVisibility
{
    private static readonly ActivityType[] Hidden =
    [
        ActivityType.MemberJoined,
        ActivityType.MemberRoleChanged,
        ActivityType.WallPhotoStaged,
        ActivityType.WallPhotoDiscarded,
    ];

    /// <summary>Gets the entries a kiosk may read (translates to SQL on both providers).</summary>
    public static Expression<Func<ActivityLogEntry, bool>> Readable { get; } = a =>
        !Hidden.Contains(a.Type)
        && (a.BoulderId == null || (!a.Boulder!.IsDraft && !a.Boulder.IsArchived));
}
