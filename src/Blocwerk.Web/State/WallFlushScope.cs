// <copyright file="WallFlushScope.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.State;

/// <summary>How much of the wall editor's pending batch the pre-deploy flush saves (see <see cref="EditorFlushPolicy.WallFlush"/>).</summary>
public enum WallFlushScope
{
    /// <summary>The whole batch, exactly as "Save Changes" would: adds, moves, deletions and "Mark as changed".</summary>
    Everything,

    /// <summary>
    /// Adds and moves only. Deletions (which can make boulders historic) and "Mark as changed" drafts (which flag
    /// boulders for review) stay the user's call: they are kept on the device and re-drafted when the editor reopens.
    /// </summary>
    AddsAndMovesOnly,
}
