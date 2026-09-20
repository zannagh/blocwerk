// <copyright file="HoldTouchupTool.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The mutually exclusive tools of the big-wall hold touch-up toolbar, mirroring the wall editor's
/// tool modes: picking one clears the others' transient state.
/// </summary>
public enum HoldTouchupTool
{
    /// <summary>
    /// No tool: a tap selects a hold and nothing else. Drags belong to the page (scrolling and
    /// pinch-zoom), NOT to the holds — this is the zero value and therefore the state every touch-up
    /// surface opens in, so it must not claim a gesture an ordinary scroll would produce.
    /// </summary>
    None,

    /// <summary>Tap an empty spot on the photo to add a hold at the current size.</summary>
    Add,

    /// <summary>
    /// Select and move: a tap selects a hold and a drag repositions it, mirroring the wall editor's
    /// <c>EditMode.Move</c>. Moving is an explicit tool for the same reason it is there — at fit zoom
    /// a finger that lands on a hold would otherwise scroll the page, so a default that claimed the
    /// drag would silently move and persist a hold.
    /// </summary>
    Move,

    /// <summary>Tap a hold to delete it.</summary>
    Delete,

    /// <summary>Tap a hold to adopt its size as the size for newly added holds.</summary>
    Pipette,
}
