// <copyright file="WallEnhancementStatus.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Entities;

/// <summary>Where a <see cref="WallEnhancementRun"/> is.</summary>
public enum WallEnhancementStatus
{
    /// <summary>The steps are running in the background (resumed after a restart).</summary>
    Running = 0,

    /// <summary>Every step ran; an admin reviews what they did.</summary>
    Review = 1,

    /// <summary>Reviewed and finalized: the generation is enhanced and the button does nothing any more.</summary>
    Finalized = 2,

    /// <summary>Stopped (the wall was updated meanwhile, or the run broke); starting again continues it.</summary>
    Failed = 3,
}
