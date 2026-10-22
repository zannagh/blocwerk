// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Linq.Expressions;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Services;

/// <summary>
/// What a kiosk tablet may read of its wall's activity log: a boulder's entries, per boulder, while that boulder is
/// published and not archived (the tablet never learns of a draft, and lists no archived boulder). The wall-wide entries
/// (holds, photos, membership) stay with the members, as the wall-wide activity card does. The boulder log read
/// (<see cref="ActivityLogService"/>) checks the boulder itself; the live feed's kiosk delivery (<c>FeedKioskActivity</c>)
/// applies this rule per entry, so the tablet's list and its live updates never disagree.
/// </summary>
public static class KioskActivityVisibility
{
    /// <summary>Gets the entries a kiosk may read (translates to SQL on both providers).</summary>
    public static Expression<Func<ActivityLogEntry, bool>> Readable { get; } = a =>
        a.BoulderId != null && !a.Boulder!.IsDraft && !a.Boulder.IsArchived;
}
