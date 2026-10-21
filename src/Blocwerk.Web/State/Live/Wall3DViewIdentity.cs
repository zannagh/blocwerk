// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Web.State.Live;

/// <summary>
/// Whether a live rebuild of a 3D view may keep the mounted one: a new view instance remounts the stage, which loses the
/// camera, the mode and the WebGL context, so a resync or a changed viewer that yields the same active model keeps it.
/// </summary>
public static class Wall3DViewIdentity
{
    /// <summary>True when <paramref name="fresh"/> shows the same active model as <paramref name="mounted"/>.</summary>
    public static bool SameModel(Wall3DViewResult? mounted, Wall3DViewResult fresh) =>
        mounted is { Status: Wall3DViewStatus.Ok, View.ModelKey: { } key }
        && fresh is { Status: Wall3DViewStatus.Ok, View: { } view }
        && view.ModelKey == key
        && view.BoulderId == mounted.View.BoulderId;
}
