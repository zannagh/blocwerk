// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Web.State.Live;

/// <summary>
/// Whether a live rebuild of a 3D view may keep the mounted one: a new view instance remounts the stage, which loses the
/// WebGL context, so a delivery resync or a changed viewer that yields the same active model keeps it. A remount of the
/// same place (<see cref="SamePlace"/>) carries the camera and the mode over.
/// </summary>
public static class Wall3DViewIdentity
{
    // What a 3D view draws besides the model: every one of these rebuilds it, coalesced, as a structural reload.
    private static readonly HashSet<string> Content =
    [
        FeedKinds.View3DChanged, FeedKinds.HoldChanged, FeedKinds.HoldDeleted, FeedKinds.VolumeChanged, FeedKinds.LinkChanged,
    ];

    /// <summary>The feed scope of a wall's 3D view: nothing patches, its content kinds rebuild it.</summary>
    public static LeafScope Scope(Guid wallId) => new(wallId, null, new HashSet<string>(), Content);

    /// <summary>True when <paramref name="fresh"/> shows the same active model as <paramref name="mounted"/>.</summary>
    public static bool SameModel(Wall3DViewResult? mounted, Wall3DViewResult fresh) =>
        mounted is { Status: Wall3DViewStatus.Ok, View.ModelKey: { } key }
        && fresh is { Status: Wall3DViewStatus.Ok, View: { } view }
        && view.ModelKey == key
        && view.BoulderId == mounted.View.BoulderId;

    /// <summary>True when a remount from <paramref name="mounted"/> to <paramref name="next"/> shows the same wall and boulder.</summary>
    public static bool SamePlace(Wall3DView? mounted, Wall3DView next) =>
        mounted is not null && mounted.WallId == next.WallId && mounted.BoulderId == next.BoulderId;
}
