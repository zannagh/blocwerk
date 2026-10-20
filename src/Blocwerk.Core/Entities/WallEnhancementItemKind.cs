// <copyright file="WallEnhancementItemKind.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Entities;

/// <summary>
/// What a review item of a wall enhancement is about. The first three were written by their step and are kept or
/// undone per hold; the others are suggestions nothing acted on yet.
/// </summary>
public enum WallEnhancementItemKind
{
    /// <summary>The hold's outline: traced from the photo and/or smoothed and separated. Undo restores the old shape.</summary>
    Shape = 0,

    /// <summary>The hold was stacked on another one (a hold on a macro or volume).</summary>
    Stack = 1,

    /// <summary>A suggested colour for a hold that had none. Keeping it confirms it.</summary>
    Colour = 2,

    /// <summary>Two holds that are probably the same hold recorded twice: merge them or not.</summary>
    Duplicate = 3,

    /// <summary>The same hold on two panels (from the 3D model): link them or not.</summary>
    Link = 4,

    /// <summary>A hold the capture photos show that the wall does not have yet: add it or not.</summary>
    NewHold = 5,
}
