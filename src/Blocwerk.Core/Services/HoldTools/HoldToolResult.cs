// <copyright file="HoldToolResult.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Services;

/// <summary>A merge or split that was written.</summary>
/// <param name="BatchId">The journal batch: undo reverts exactly this.</param>
/// <param name="KeptHoldId">The hold that kept its identity.</param>
/// <param name="OtherHoldId">The merged-away hold (merge) or the new part (split).</param>
/// <param name="MovedBoulders">Boulders whose membership moved.</param>
/// <param name="FlaggedForReview">Boulders flagged for review (a merge where a boulder used both holds).</param>
public sealed record HoldToolResult(Guid BatchId, Guid KeptHoldId, Guid OtherHoldId, int MovedBoulders, int FlaggedForReview);
