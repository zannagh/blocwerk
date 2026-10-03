// <copyright file="WallRefreshStatus.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Entities;

/// <summary>Lifecycle of a <see cref="WallRefresh"/> ("Update panels + 3D"). Stored as its integer value; never renumber.</summary>
public enum WallRefreshStatus
{
    /// <summary>Photos and videos are being dropped in.</summary>
    Uploading = 0,

    /// <summary>The worker compares the photos with the current panel photos.</summary>
    Sorting = 1,

    /// <summary>A photo is proposed for each panel; the user checks the choice and starts.</summary>
    ReadyToStart = 2,

    /// <summary>The worker starts the 3D capture and prepares the panel update.</summary>
    Running = 3,

    /// <summary>The panel update is prepared; waiting for the user's confirm.</summary>
    ReadyToApply = 4,

    /// <summary>The worker applies the panel update and places the holds on the 3D model.</summary>
    Applying = 5,

    /// <summary>Finished (the 3D capture may still be running on its own).</summary>
    Done = 6,

    /// <summary>Stopped with an error (see Error).</summary>
    Failed = 7,

    /// <summary>Thrown away by the user.</summary>
    Discarded = 8,
}
