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

    // ---- Wall-editor tools -----------------------------------------------------------------
    // The wall editor's own EditMode used to be a second, parallel enum with no None value, so "no
    // tool" was unrepresentable there and nothing could be shared with the touch-up surfaces. Its
    // members live here instead; a surface that does not offer one simply never puts it in its spec.

    /// <summary>Tap a hold to edit its traced outline, then drag the control points.</summary>
    Shape,

    /// <summary>Tap holds to stamp the current paint colour onto them.</summary>
    Paint,

    /// <summary>Tap a hold to name it.</summary>
    Name,

    /// <summary>Drag the wall-border polygon's points.</summary>
    Border,

    /// <summary>Tap a hold, then drag its handle to tilt the polygon outline in X/Y.</summary>
    ShapeTilt,

    /// <summary>Tap a new hold then the matching old one to merge them (staged wall updates).</summary>
    Merge,

    /// <summary>Tap a virtual hold to promote it, or merge it into a detected hold.</summary>
    MakeActual,

    /// <summary>Tap the virtual hold to keep, then the duplicate to absorb into it.</summary>
    JoinVirtual,

    /// <summary>Tap a hold that did not physically change, to clear its flags.</summary>
    MarkUnchanged,

    /// <summary>Tap a hold that was physically swapped or re-set, to flag it and its boulders.</summary>
    MarkChanged,
}
