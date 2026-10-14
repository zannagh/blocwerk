// <copyright file="RedetectSplits.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Redetect;

/// <summary>
/// Finds the stored holds a re-detect sees as several holds: an outline that once bled from one hold onto its
/// neighbour is one stored hold where the detector now finds two. Pure. A stored hold is split when at least two
/// detections lie inside it, each a real share of it, and together they cover most of it; a small hold ON a large
/// one covers little of it and is a stacked hold, not a piece.
/// </summary>
internal static class RedetectSplits
{
    /// <summary>A detection is a piece of a stored hold when at least this share of it lies inside the hold.</summary>
    public const double MinInside = 0.6;

    /// <summary>...and it is at least this share of the hold's area.</summary>
    public const double MinPieceShare = 0.15;

    /// <summary>...and at most this share: beyond it the detection is the hold itself, drawn a little smaller.</summary>
    public const double MaxPieceShare = 0.8;

    /// <summary>The pieces together cover at least this share of the stored hold.</summary>
    public const double MinCovered = 0.6;

    /// <summary>The stored holds that are several detections.</summary>
    /// <param name="input">The stored holds and the detections.</param>
    /// <param name="owners">Per detection that a stored hold already takes its shape from, that hold.</param>
    /// <returns>The splits, by stored hold id.</returns>
    public static List<RedetectSplit> Find(RedetectInput input, IReadOnlyDictionary<int, Guid> owners)
    {
        var polygons = input.Detections.Select(d => HoldShapePolygon.Of(d.Shape)).ToList();
        var boxes = polygons.Select(Box).ToList();
        var splits = new List<RedetectSplit>();
        foreach (var existing in input.Existing.Where(e => RedetectPlanner.IsMatchable(e.Hold)).OrderBy(e => e.Hold.Id))
        {
            var pieces = Pieces(existing.Hold, polygons, boxes, owners);
            if (pieces.Count >= 2 && pieces.Sum(p => p.Share) >= MinCovered)
            {
                splits.Add(new RedetectSplit(existing.Hold.Id, [.. pieces.OrderByDescending(p => p.Share).ThenBy(p => p.Index).Select(p => p.Index)]));
            }
        }

        return splits;
    }

    private static List<(int Index, double Share)> Pieces(
        Hold hold, List<P2[]> polygons, List<(double MinX, double MinY, double MaxX, double MaxY)> boxes, IReadOnlyDictionary<int, Guid> owners)
    {
        var outline = HoldShapePolygon.Of(hold);
        var box = Box(outline);
        var pieces = new List<(int, double)>();
        for (int i = 0; i < polygons.Count; i++)
        {
            if (boxes[i].MaxX < box.MinX || box.MaxX < boxes[i].MinX || boxes[i].MaxY < box.MinY || box.MaxY < boxes[i].MinY
                || (owners.TryGetValue(i, out var owner) && owner != hold.Id))
            {
                continue;
            }

            var (area, pieceArea, shared) = ShapeOverlapArea.Measure(outline, polygons[i]);
            if (area > 0 && pieceArea > 0 && shared >= MinInside * pieceArea
                && pieceArea >= MinPieceShare * area && pieceArea <= MaxPieceShare * area)
            {
                pieces.Add((i, shared / area));
            }
        }

        return pieces;
    }

    private static (double MinX, double MinY, double MaxX, double MaxY) Box(IReadOnlyList<P2> poly) =>
        (poly.Min(p => p.X), poly.Min(p => p.Y), poly.Max(p => p.X), poly.Max(p => p.Y));
}
