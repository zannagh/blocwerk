// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Contracts.Feed;

/// <summary>
/// Every feed kind (design §5.4). A kind names what changed; the state itself is read back by id (hydration or a scoped
/// reload), so an event is a hint, never an absolute overwrite. Clients ignore kinds they do not know.
/// </summary>
public static class FeedKinds
{
    public const string WallUpdated = "wall.updated";
    public const string WallManifest = "wall.manifest";

    /// <summary>Something on the wall changed in bulk (a scope, a large save, a failed mapping): reload the wall.</summary>
    public const string WallResync = "wall.resync";

    public const string MemberChanged = "member.changed";
    public const string AccessRevoked = "access.revoked";
    public const string SegmentChanged = "segment.changed";
    public const string PanelChanged = "panel.changed";

    /// <summary>
    /// Only a panel's staged photo changed (the update procedure): for the editors, so a staged-only save still wakes the
    /// wall. Nothing a viewer has loaded shows a staged photo, so it reloads nothing.
    /// </summary>
    public const string PanelStaged = "panel.staged";
    public const string LinkChanged = "link.changed";
    public const string VolumeChanged = "volume.changed";
    public const string HoldChanged = "hold.changed";
    public const string HoldDeleted = "hold.deleted";
    public const string BoulderChanged = "boulder.changed";
    public const string BoulderDeleted = "boulder.deleted";

    /// <summary>Ticks, ratings or grade proposals of a boulder changed; counts are computed at read time.</summary>
    public const string BoulderStats = "boulder.stats";

    public const string CommentAdded = "comment.added";
    public const string CommentDeleted = "comment.deleted";
    public const string VideoAdded = "video.added";
    public const string VideoStatus = "video.status";
    public const string VideoDeleted = "video.deleted";
    public const string GalleryChanged = "gallery.changed";
    public const string GalleryDeleted = "gallery.deleted";
    public const string View3DChanged = "view3d.changed";
    public const string ActivityAdded = "activity.added";
    public const string FavoriteChanged = "favorite.changed";
    public const string RatingMine = "rating.mine";
    public const string TickMine = "tick.mine";
    public const string UserProfile = "user.profile";

    /// <summary>A user's own ascents changed in bulk (an external import): reload them.</summary>
    public const string TicksResync = "ticks.resync";

    /// <summary>A user's own state changed in bulk (an account merged into theirs): their devices reload all of it.</summary>
    public const string UserResync = "user.resync";

    /// <summary>
    /// The user's set of walls changed: they were added to <c>WallId</c>, or created it. Addressed to that user alone, who
    /// had no access before, so no subscription of theirs reaches it through the wall: their wall list reloads.
    /// </summary>
    public const string WallsChanged = "walls.changed";

    /// <summary>Gets every known kind.</summary>
    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        WallUpdated, WallManifest, WallResync, MemberChanged, AccessRevoked, SegmentChanged, PanelChanged, LinkChanged,
        VolumeChanged, HoldChanged, HoldDeleted, BoulderChanged, BoulderDeleted, BoulderStats, CommentAdded, CommentDeleted,
        VideoAdded, VideoStatus, VideoDeleted, GalleryChanged, GalleryDeleted, View3DChanged, ActivityAdded, FavoriteChanged,
        RatingMine, TickMine, UserProfile, TicksResync, UserResync, PanelStaged, WallsChanged,
    };

    /// <summary>True for a kind this assembly knows; a client ignores the rest.</summary>
    public static bool IsKnown(string? kind) => kind is not null && All.Contains(kind);
}
