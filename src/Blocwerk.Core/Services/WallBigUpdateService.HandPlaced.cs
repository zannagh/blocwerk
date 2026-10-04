// <copyright file="WallBigUpdateService.HandPlaced.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Services;

/// <summary>
/// Hand-placed and virtual holds that the new photo finally detects: the matcher cannot pair them (a virtual hold has
/// no pixels, a hand-placed one may not look like a detection), so they would otherwise stay as they were beside a
/// duplicate new detection. See <see cref="HandPlacedMerger"/> for when they are merged and when a person decides.
/// </summary>
public partial class WallBigUpdateService
{
    /// <summary>
    /// Adds a carryover proposal for every unambiguous pair (the old hold keeps its identity and takes the detection
    /// as its twin) and takes both out of the unmatched lists. Centre panel only, like the rest of the reviewable scope.
    /// </summary>
    private static HandPlacedResult MergeHandPlaced(
        IReadOnlyList<Hold> centreDetections,
        IReadOnlyList<Hold> centreOldHolds,
        byte[] centreImage,
        List<CarryoverProposal> carryover,
        List<Guid> removedCandidates,
        List<Guid> newCenter,
        IReadOnlyDictionary<Guid, HoldPositionNorm> warp)
    {
        var unpairedOldIds = removedCandidates.ToHashSet();
        var unpairedNewIds = newCenter.ToHashSet();
        var result = HandPlacedMerger.Find(
            centreOldHolds.Where(h => unpairedOldIds.Contains(h.Id)).ToList(),
            centreDetections.Where(h => unpairedNewIds.Contains(h.Id)).ToList(),
            warp,
            OverlapSeedLoader.RawSize(centreImage));
        foreach (var merge in result.Merges)
        {
            carryover.Add(new CarryoverProposal(merge.OldHoldId, merge.NewHoldId, 1.0, 0));
            removedCandidates.Remove(merge.OldHoldId);
            newCenter.Remove(merge.NewHoldId);
        }

        return result;
    }
}
