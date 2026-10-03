// <copyright file="Wall3DHoldCountText.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry.View3D;

/// <summary>
/// The hold count a 3D view shows: physical holds drawn, with how many of them overlapping panels
/// each stored a copy of, so the number matches what is on the wall rather than the stored rows.
/// </summary>
public static class Wall3DHoldCountText
{
    /// <summary>E.g. "761 holds (119 seen on two panels)".</summary>
    /// <param name="view">The view.</param>
    /// <returns>The text.</returns>
    public static string Format(Wall3DView view)
    {
        var count = view.Holds.Count == 1 ? "1 hold" : $"{view.Holds.Count} holds";
        if (view.MultiPanelHoldCount == 0)
        {
            return count;
        }

        var many = view.Holds.Any(h => h.DuplicateIds is { Count: > 1 });
        return $"{count} ({view.MultiPanelHoldCount} seen on {(many ? "more than one panel" : "two panels")})";
    }
}
