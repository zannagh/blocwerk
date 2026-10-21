// <copyright file="WallFlushResult.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Web.State;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// What the hold editor's pre-deploy flush did, and the decisions it left for the page to keep on the device (only
/// under <see cref="WallFlushScope.AddsAndMovesOnly"/>: deletions and "Mark as changed" drafts).
/// </summary>
public sealed record WallFlushResult(EditFlushOutcome Outcome, IReadOnlyList<Guid> KeepDeleted, IReadOnlyList<Guid> KeepMarked)
{
    /// <summary>Whether anything is left to keep on the device.</summary>
    public bool HasKept => KeepDeleted.Count > 0 || KeepMarked.Count > 0;
}
