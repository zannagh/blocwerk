// <copyright file="StagedTwinDeletion.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Enums;

/// <summary>
/// What deleting a staged hold does when an old hold's carry verdict names it as the old hold's new position (its
/// twin). The decision row's twin is a cascading foreign key, so without a choice the delete silently took the old
/// hold's verdict (a "Changed", a confirmation) with it.
/// </summary>
public enum StagedTwinDeletion
{
    /// <summary>The delete is refused (<see cref="Services.StagedHoldIsTwinException"/>) and nothing changes.</summary>
    Refuse = 0,

    /// <summary>
    /// The verdicts keep their kind but lose the twin: the old hold is carried at its own (warped) position, and the
    /// verdict reads as unreviewed again, since the sign-off was about the match that went away.
    /// </summary>
    ReleaseTwin = 1,
}
