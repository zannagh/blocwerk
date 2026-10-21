// <copyright file="WallFlushResult.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Web.State;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// What the hold editor's pre-deploy flush did: the outcome, the boulders the saved deletions retired and the ones the
/// saved "Mark as changed" flagged for review (so the notice can name and link them), and, only under
/// <see cref="WallFlushScope.AddsAndMovesOnly"/>, the decisions left for the page to keep on the device.
/// </summary>
public sealed record WallFlushResult(
    EditFlushOutcome Outcome,
    IReadOnlyList<Guid> KeepDeleted,
    IReadOnlyList<Guid> KeepMarked,
    IReadOnlyList<AffectedBoulder> Retired,
    IReadOnlyList<AffectedBoulder> FlaggedForReview)
{
    /// <summary>A result with nothing saved and nothing affected.</summary>
    public static WallFlushResult Plain(EditFlushOutcome outcome) => new(outcome, [], [], [], []);

    /// <summary>Whether anything is left to keep on the device.</summary>
    public bool HasKept => KeepDeleted.Count > 0 || KeepMarked.Count > 0;
}
