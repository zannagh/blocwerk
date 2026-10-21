// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Feed;

/// <summary>
/// Whom a subscription delivers to: the audiences the viewer belongs to on the wall, and the signed-in user (for
/// <see cref="FeedAudience.User"/> rows, which go to their own user only). Access to the wall itself is checked by the
/// caller before it subscribes.
/// </summary>
public sealed class FeedViewer
{
    public FeedViewer(IEnumerable<FeedAudience> audiences, Guid? userId)
    {
        Audiences = audiences.Where(a => a != FeedAudience.User).ToHashSet();
        UserId = userId;
    }

    /// <summary>Gets the shared audiences (<see cref="FeedAudience.User"/> is decided by <see cref="UserId"/>).</summary>
    public IReadOnlySet<FeedAudience> Audiences { get; }

    public Guid? UserId { get; }

    /// <summary>A share-link or anonymous kiosk viewer.</summary>
    public static FeedViewer Anonymous() => new([FeedAudience.Wall], null);

    /// <summary>
    /// A kiosk tablet bound to the wall, whoever it is acting as. It never carries the acting member's id: the tablet is
    /// shared, so a member's own rows (<c>tick.mine</c>, <c>rating.mine</c>, <c>favorite.changed</c>) must not reach it.
    /// </summary>
    public static FeedViewer Kiosk() => new([FeedAudience.Wall], null);

    /// <summary>A member of the wall.</summary>
    public static FeedViewer Member(Guid userId) => new([FeedAudience.Wall, FeedAudience.Members], userId);

    /// <summary>A setter or admin of the wall (sees drafts).</summary>
    public static FeedViewer Setter(Guid userId) => new([FeedAudience.Wall, FeedAudience.Members, FeedAudience.Setters], userId);

    /// <summary>True when <paramref name="row"/> may reach this viewer.</summary>
    public bool Allows(WallChange row) => row.Audience == FeedAudience.User
        ? UserId is { } user && row.AudienceUserId == user
        : Audiences.Contains(row.Audience);
}
