// <copyright file="HandPlacedMerger.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Services;

/// <summary>A hand-placed or virtual old hold and the one detection it is merged with.</summary>
/// <param name="OldHoldId">The old hold, which keeps its identity.</param>
/// <param name="NewHoldId">The staged detection whose position, outline and size it adopts.</param>
public sealed record HandPlacedMerge(Guid OldHoldId, Guid NewHoldId);

/// <summary>A hand-placed or virtual old hold whose spot is contested, listed on the confirm screen as a case to decide.</summary>
/// <param name="OldHoldId">The old hold.</param>
/// <param name="NewHoldIds">The detections at its spot (several), or the one detection that also overlaps another old hold.</param>
public sealed record HandPlacedAmbiguity(Guid OldHoldId, IReadOnlyList<Guid> NewHoldIds);

/// <summary>What <see cref="HandPlacedMerger"/> found.</summary>
/// <param name="Merges">Exactly one detection for exactly one old hold.</param>
/// <param name="Ambiguous">Contested spots, left alone.</param>
public sealed record HandPlacedResult(IReadOnlyList<HandPlacedMerge> Merges, IReadOnlyList<HandPlacedAmbiguity> Ambiguous)
{
    /// <summary>Nothing found.</summary>
    public static readonly HandPlacedResult None = new([], []);
}

/// <summary>
/// Pairs a hand-placed (not auto-detected) or virtual old hold that the matcher could not pair with a detection
/// that now sits on its spot: the owner placed it by hand because detection missed it, and the new photo finally
/// detects it. A pair is merged only when it is unambiguous both ways (the old hold overlaps exactly one detection
/// and that detection overlaps no other such old hold); otherwise it is reported for a person. Pure and deterministic.
/// </summary>
public static class HandPlacedMerger
{
    /// <summary>Whether <paramref name="hold"/> is one the owner placed by hand (or that has no pixels at all).</summary>
    /// <param name="hold">The old hold.</param>
    /// <returns>True for hand-placed or virtual holds.</returns>
    public static bool IsHandPlaced(Hold hold) => hold.IsVirtual || !hold.IsAutoDetected;

    /// <summary>
    /// How close (fraction of the photo's longer side) a detection must be to a VIRTUAL hold. A virtual hold has no pixels and
    /// no real extent, so its stored radius means nothing; this is a small fixed reach (about 40 mm on a panel photo).
    /// </summary>
    public const double VirtualReach = 0.012;

    /// <summary>A real hold's detection must be within this fraction of the hold's radius.</summary>
    public const double RadiusReach = 0.5;

    /// <summary>The detection's radius must be within this factor of a real hold's radius.</summary>
    public const double SizeFactor = 2.0;

    /// <summary>Finds the merges and the contested spots.</summary>
    /// <param name="unpairedOld">Old holds on the panel the matcher found no twin for.</param>
    /// <param name="unpairedNew">Detections on the panel no old hold was paired with.</param>
    /// <param name="warp">The matcher's predicted new-image position per old hold, when it has one.</param>
    /// <param name="size">The staged photo's pixel size, to compare in pixels; null compares normalised.</param>
    /// <returns>The result.</returns>
    public static HandPlacedResult Find(
        IReadOnlyList<Hold> unpairedOld,
        IReadOnlyList<Hold> unpairedNew,
        IReadOnlyDictionary<Guid, HoldPositionNorm>? warp,
        (int Width, int Height)? size)
    {
        var olds = unpairedOld.Where(IsHandPlaced).OrderBy(h => h.Id).ToList();
        var news = unpairedNew.OrderBy(h => h.Id).ToList();
        var near = olds.ToDictionary(o => o.Id, o => news.Where(n => Overlaps(o, n, warp, size)).Select(n => n.Id).ToList());
        var claimants = news.ToDictionary(n => n.Id, n => olds.Where(o => near[o.Id].Contains(n.Id)).Select(o => o.Id).ToList());

        var merges = new List<HandPlacedMerge>();
        var ambiguous = new List<HandPlacedAmbiguity>();
        foreach (var old in olds)
        {
            var candidates = near[old.Id];
            if (candidates.Count == 1 && claimants[candidates[0]].Count == 1)
            {
                merges.Add(new HandPlacedMerge(old.Id, candidates[0]));
            }
            else if (candidates.Count > 0)
            {
                ambiguous.Add(new HandPlacedAmbiguity(old.Id, candidates));
            }
        }

        return new HandPlacedResult(merges, ambiguous);
    }

    private static bool Overlaps(
        Hold old, Hold detection, IReadOnlyDictionary<Guid, HoldPositionNorm>? warp, (int Width, int Height)? size)
    {
        var expected = warp is not null && warp.TryGetValue(old.Id, out var w) ? (w.X, w.Y) : (old.X, old.Y);
        var (sx, sy, sr) = size is { } s ? (s.Width, s.Height, Math.Max(s.Width, s.Height)) : (1.0, 1.0, 1.0);
        var dx = (detection.X - expected.Item1) * sx;
        var dy = (detection.Y - expected.Item2) * sy;
        var reach = (old.IsVirtual ? VirtualReach : RadiusReach * old.Radius) * sr;
        return (dx * dx) + (dy * dy) <= reach * reach && Alike(old, detection);
    }

    /// <summary>Similar size and, when both are known, the same colour. A virtual hold has no size to compare.</summary>
    private static bool Alike(Hold old, Hold detection)
    {
        if (!old.IsVirtual && old.Radius > 0 && detection.Radius > 0
            && (detection.Radius > old.Radius * SizeFactor || detection.Radius < old.Radius / SizeFactor))
        {
            return false;
        }

        return old.Color is null || detection.Color is null || string.Equals(old.Color, detection.Color, StringComparison.OrdinalIgnoreCase);
    }
}
