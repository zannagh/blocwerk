// <copyright file="WallBigUpdateService.HandPlaced.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Services;

/// <summary>
/// Hand-placed and virtual holds that the new photo finally detects: the matcher cannot pair them (a virtual hold has
/// no pixels, a hand-placed one may not look like a detection), so they would otherwise stay as they were beside a
/// duplicate new detection. See <see cref="HandPlacedMerger"/> for when they are merged and when a person decides.
/// Runs per re-photographed panel, but only where the panel's photos were aligned: on a panel the matcher could not line
/// up there is no warp-predicted spot, and the old coordinates mean nothing on the new photo.
/// </summary>
public partial class WallBigUpdateService
{
    /// <summary>
    /// Adds a carryover proposal for every unambiguous pair on one panel (the old hold keeps its identity and takes the
    /// detection as its twin), takes both out of the unmatched lists and reports the merges and the contested spots.
    /// </summary>
    /// <param name="detections">The panel's staged detections.</param>
    /// <param name="oldHolds">The panel's carried old holds.</param>
    /// <param name="image">The staged photo, for its pixel size.</param>
    /// <param name="carryover">The session's proposals (merges are added).</param>
    /// <param name="removedCandidates">Old holds with no twin (merged ones are taken out).</param>
    /// <param name="unpairedNew">The panel's unpaired detections when the caller tracks them; null derives them from <paramref name="carryover"/>.</param>
    /// <param name="warp">The matcher's predicted spots.</param>
    /// <param name="merges">Collects the merges.</param>
    /// <param name="ambiguous">Collects the contested spots.</param>
    private static void MergeHandPlaced(
        IReadOnlyList<Hold> detections,
        IReadOnlyList<Hold> oldHolds,
        byte[] image,
        List<CarryoverProposal> carryover,
        List<Guid> removedCandidates,
        List<Guid>? unpairedNew,
        IReadOnlyDictionary<Guid, HoldPositionNorm> warp,
        List<HandPlacedMerge> merges,
        List<HandPlacedAmbiguity> ambiguous)
    {
        var unpairedOldIds = removedCandidates.ToHashSet();
        var paired = carryover.Select(c => c.NewHoldId).ToHashSet();
        var unpairedNewIds = (unpairedNew ?? detections.Where(d => !paired.Contains(d.Id)).Select(d => d.Id).ToList()).ToHashSet();
        var result = HandPlacedMerger.Find(
            oldHolds.Where(h => unpairedOldIds.Contains(h.Id)).ToList(),
            detections.Where(h => unpairedNewIds.Contains(h.Id)).ToList(),
            warp,
            OverlapSeedLoader.RawSize(image));
        foreach (var merge in result.Merges)
        {
            carryover.Add(new CarryoverProposal(merge.OldHoldId, merge.NewHoldId, 1.0, 0));
            removedCandidates.Remove(merge.OldHoldId);
            unpairedNew?.Remove(merge.NewHoldId);
        }

        merges.AddRange(result.Merges);
        ambiguous.AddRange(result.Ambiguous);
    }
}
