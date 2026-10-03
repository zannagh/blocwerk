// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Geometry.View3D;

/// <summary>Why a 3D view could or could not be built.</summary>
public enum Wall3DViewStatus
{
    /// <summary>The view was built; <see cref="Wall3DViewResult.View"/> is set.</summary>
    Ok,

    /// <summary>No such wall, or the viewer may not see it (not a member, wrong share token).</summary>
    NotFound,

    /// <summary>The wall is in update mode and hidden from everyone but the updating admin.</summary>
    UnderMaintenance,

    /// <summary>The wall has no active geometry model.</summary>
    NoGeometry,

    /// <summary>The active model could not be parsed, or has a schema version this build does not know.</summary>
    InvalidGeometry,
}

/// <summary>The outcome of <see cref="Abstractions.IWall3DViewService.BuildAsync"/>.</summary>
/// <param name="Status">What happened.</param>
/// <param name="WallName">The wall's name whenever the viewer may see the wall (null for <see cref="Wall3DViewStatus.NotFound"/>).</param>
/// <param name="View">The view, only for <see cref="Wall3DViewStatus.Ok"/>.</param>
public sealed record Wall3DViewResult(Wall3DViewStatus Status, string? WallName = null, Wall3DView? View = null);
