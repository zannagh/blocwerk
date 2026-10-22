// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Feed;

/// <summary>
/// Whom a subscription delivers to: the audiences the viewer belongs to on the wall, and the signed-in user (for
/// <see cref="FeedAudience.User"/> rows, which go to their own user only). Access to the wall itself is checked by the
/// caller before it subscribes. A viewer may also read some kinds of <see cref="FeedAudience.Members"/> rows without being a
/// member (<see cref="MemberKinds"/>): those rows are narrowed again at delivery (<see cref="FeedKioskActivity"/>), so their
/// stored audience never has to widen.
/// </summary>
public sealed class FeedViewer
{
    public FeedViewer(IEnumerable<FeedAudience> audiences, Guid? userId, IEnumerable<string>? memberKinds = null)
    {
        Audiences = audiences.Where(a => a != FeedAudience.User).ToHashSet();
        UserId = userId;
        MemberKinds = Audiences.Contains(FeedAudience.Members) ? new HashSet<string>() : (memberKinds ?? []).ToHashSet();
    }

    /// <summary>Gets the shared audiences (<see cref="FeedAudience.User"/> is decided by <see cref="UserId"/>).</summary>
    public IReadOnlySet<FeedAudience> Audiences { get; }

    public Guid? UserId { get; }

    /// <summary>
    /// Gets the kinds of <see cref="FeedAudience.Members"/> rows this viewer reads although it is no member, each still
    /// subject to the delivery-time rule of <see cref="FeedKioskActivity"/>. Empty for a member, who reads them all anyway.
    /// </summary>
    public IReadOnlySet<string> MemberKinds { get; }

    /// <summary>A share-link or anonymous kiosk viewer.</summary>
    public static FeedViewer Anonymous() => new([FeedAudience.Wall], null);

    /// <summary>
    /// A kiosk tablet bound to the wall, whoever it is acting as. It never carries the acting member's id: the tablet is
    /// shared, so a member's own rows (<c>tick.mine</c>, <c>rating.mine</c>, <c>favorite.changed</c>) must not reach it.
    /// It reads the wall's activity (<c>activity.added</c>, a members row) as far as <see cref="FeedKioskActivity"/> lets
    /// it; a share link does not.
    /// </summary>
    public static FeedViewer Kiosk() => new([FeedAudience.Wall], null, [FeedKinds.ActivityAdded]);

    /// <summary>A member of the wall.</summary>
    public static FeedViewer Member(Guid userId) => new([FeedAudience.Wall, FeedAudience.Members], userId);

    /// <summary>A setter or admin of the wall (sees drafts).</summary>
    public static FeedViewer Setter(Guid userId) => new([FeedAudience.Wall, FeedAudience.Members, FeedAudience.Setters], userId);

    /// <summary>True when <paramref name="row"/> may reach this viewer.</summary>
    public bool Allows(WallChange row) => row.Audience == FeedAudience.User
        ? UserId is { } user && row.AudienceUserId == user
        : Audiences.Contains(row.Audience);
}
