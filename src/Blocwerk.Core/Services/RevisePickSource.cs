// <copyright file="RevisePickSource.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Services;

/// <summary>Why the Revise editor pre-selected a current hold for one of the boulder's own holds.</summary>
public enum RevisePickSource
{
    /// <summary>The boulder's own hold is still live: no translation was needed.</summary>
    Carried = 0,

    /// <summary>Reached by walking the hold's lineage forward through links that all say the hold stayed the same.</summary>
    Lineage = 1,

    /// <summary>Reached through lineage, but a link on the way records that the physical hold changed (moved, reshaped, replaced).</summary>
    LineageChanged = 2,

    /// <summary>
    /// The hold has no lineage at all, so the nearest unclaimed hold on the same panel position was taken.
    /// A guess the reviser has to confirm.
    /// </summary>
    Guessed = 3,
}
