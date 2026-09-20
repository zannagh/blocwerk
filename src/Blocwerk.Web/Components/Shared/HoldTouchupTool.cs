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
    /// <summary>No tool: taps select a hold, drags move it.</summary>
    None,

    /// <summary>Tap an empty spot on the photo to add a hold at the current size.</summary>
    Add,

    /// <summary>Tap a hold to delete it.</summary>
    Delete,

    /// <summary>Tap a hold to adopt its size as the size for newly added holds.</summary>
    Pipette,
}
