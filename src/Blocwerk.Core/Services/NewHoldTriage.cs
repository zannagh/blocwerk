// <copyright file="NewHoldTriage.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

/// <summary>An unpaired staged detection, in RAW pixels of the staged photo (radius 0: size unknown).</summary>
public readonly record struct TriageCandidate(Guid Id, double X, double Y, double RadiusPx = 0, bool Confident = false);

/// <summary>A hold's spot on a staged photo, RAW pixels.</summary>
public readonly record struct TriageSpot(double X, double Y, double RadiusPx);

/// <summary>What the triage of one staged panel knows (all RAW pixels).</summary>
/// <param name="Candidates">The unpaired auto-detected staged holds.</param>
/// <param name="NewToOld">Matched pairs, staged photo → old photo. Empty when the panel was not aligned.</param>
/// <param name="OldSize">The old photo's size, or null when there is none.</param>
/// <param name="MarkerQuads">Printed-marker corners detected on the staged photo.</param>
/// <param name="Owner">For a neighbour panel: the centre panel that owns the overlap with it. Null for the centre.</param>
/// <param name="Evidence3D">What the 3D model says per candidate; null (or a missing id) when there is no 3D evidence.</param>
/// <param name="Carried">The staged detections an old hold is carried onto.</param>
/// <param name="NotFound">The old holds no detection carries, at their predicted spot on the staged photo.</param>
public sealed record NewHoldTriageInput(
    IReadOnlyList<TriageCandidate> Candidates,
    IReadOnlyList<PointPair> NewToOld,
    (int Width, int Height)? OldSize,
    IReadOnlyList<IReadOnlyList<(double X, double Y)>> MarkerQuads,
    OverlapOwner? Owner = null,
    IReadOnlyDictionary<Guid, Evidence3DVerdict>? Evidence3D = null,
    IReadOnlyList<TriageSpot>? Carried = null,
    IReadOnlyList<TriageSpot>? NotFound = null);

/// <summary>
/// Picks the unpaired staged detections that are most likely NOT new holds, so the review discards them
/// by default (the user can still keep any of them). Conservative on purpose: a detection is only
/// suggested when it sits on a printed marker, when (on a neighbour panel) the centre photo shows that spot,
/// when the aligned old photo did not cover it, or when the old photo shows the same thing at the aligned spot.
/// A detection on the spot of an old hold that was NOT found again is never "known" or "unchanged": it is what is
/// there now, so it stays a new hold. Nor is a detection "known" for a carried hold of a clearly different size.
/// </summary>
public static class NewHoldTriage
{
    /// <summary>How far (fraction of the old photo) outside its frame a spot must map to count as uncovered.</summary>
    public const double OutsideMargin = 0;

    /// <summary>Correlation at or above which the old photo already showed the same thing.</summary>
    public const double SameAsOldScore = 0.6;

    /// <summary>The detector confidence from which a detection with a traced outline is never dropped as unchanged.</summary>
    public const double ConfidentDetection = 0.9;

    /// <summary>True for a detection the detector is sure of and whose outline was really traced (not a circle, not a rough shape).</summary>
    /// <param name="confidence">The detector's confidence.</param>
    /// <param name="outlined">Whether the hold has an outline.</param>
    /// <param name="outlineConfidence">The outliner's confidence, when it gave one.</param>
    /// <returns>Whether the detection is confident and plausible.</returns>
    public static bool IsConfident(double confidence, bool outlined, double? outlineConfidence) =>
        confidence >= ConfidentDetection && outlined && outlineConfidence is > Detection.Outlines.HoldOutlineRefiner.CircleConfidenceCeiling;

    /// <summary>The marker quad grown by this factor also covers its white print border.</summary>
    public const double MarkerGrowth = 1.35;

    /// <summary>Radii further apart than this factor (either way) are not the same hold.</summary>
    public const double SameHoldSizeFactor = 1.8;

    /// <summary>
    /// From this many fixed patch half-sides on, the fixed patch lies wholly inside the hold and shows only its surface;
    /// the presence question is then asked with the hold's radius, so its outline is compared.
    /// </summary>
    public const double LargeHoldPatches = 2;

    private const int MinimumPairs = 6;

    /// <summary>The radius the presence question about a detection carries: its own when it is large, else none.</summary>
    /// <param name="radiusPx">The detection's radius on the new photo, px.</param>
    /// <returns>The radius for the <see cref="PresenceQuery"/>, 0 for the fixed patch.</returns>
    public static double ProbeRadiusPx(double radiusPx) =>
        radiusPx >= LargeHoldPatches * PresencePatch.DefaultRadius ? radiusPx : 0;

    public static Dictionary<Guid, NewHoldDiscardReason> Classify(
        NewHoldTriageInput input,
        Func<IReadOnlyList<PresenceQuery>, IReadOnlyList<double?>>? presence)
    {
        var result = new Dictionary<Guid, NewHoldDiscardReason>();
        var aligned = input.OldSize is not null && input.NewToOld.Count >= MinimumPairs;
        var queries = new List<(Guid Id, PresenceQuery Query)>();
        foreach (var c in input.Candidates)
        {
            if (input.MarkerQuads.Any(q => InsideGrown(q, c.X, c.Y)))
            {
                result[c.Id] = NewHoldDiscardReason.OnMarker;
                continue;
            }

            if (input.Owner is { } owner && NeighbourPanelRule.ShownByOwner(owner, c.X, c.Y))
            {
                result[c.Id] = NewHoldDiscardReason.SeenOnNeighbourPanel;
                continue;
            }

            var onNotFound = OnSpot(input.NotFound, c).Any();
            if (Discard3D(input.Evidence3D, c.Id) is { } reason3D
                && !(reason3D == NewHoldDiscardReason.KnownHoldIn3D && (onNotFound || OtherSizeThanCarried(input.Carried, c))))
            {
                result[c.Id] = reason3D;
                continue;
            }

            if (!aligned || LocalAffine.Predict(input.NewToOld, c.X, c.Y) is not { } old)
            {
                continue;
            }

            var (w, h) = input.OldSize!.Value;
            if (old.X < -OutsideMargin * w || old.Y < -OutsideMargin * h
                || old.X > (1 + OutsideMargin) * w || old.Y > (1 + OutsideMargin) * h)
            {
                result[c.Id] = NewHoldDiscardReason.OutsideOldPhoto;
                continue;
            }

            // A detection the detector is sure of and that has a traced outline is a hold, also where the spot looks
            // as it did on the old photo: the old wall simply had no record of it (or only a placeholder circle).
            if (onNotFound || c.Confident)
            {
                continue;
            }

            var scale = LocalAffine.Scale(input.NewToOld, c.X, c.Y) ?? 1;
            queries.Add((c.Id, new PresenceQuery(c.X, c.Y, old.X, old.Y, scale, ProbeRadiusPx(c.RadiusPx))));
        }

        AddUnchanged(result, queries, presence);
        return result;
    }

    /// <summary>Point-in-polygon (even-odd) against the quad grown about its centre.</summary>
    public static bool InsideGrown(IReadOnlyList<(double X, double Y)> quad, double x, double y)
    {
        if (quad.Count < 3)
        {
            return false;
        }

        var cx = quad.Average(p => p.X);
        var cy = quad.Average(p => p.Y);
        var grown = quad.Select(p => (X: cx + ((p.X - cx) * MarkerGrowth), Y: cy + ((p.Y - cy) * MarkerGrowth))).ToList();
        var inside = false;
        for (int i = 0, j = grown.Count - 1; i < grown.Count; j = i++)
        {
            var (xi, yi) = grown[i];
            var (xj, yj) = grown[j];
            if ((yi > y) != (yj > y) && x < ((xj - xi) * (y - yi) / (yj - yi)) + xi)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>The spots the candidate sits on: one centre lies within the other's radius.</summary>
    private static IEnumerable<TriageSpot> OnSpot(IReadOnlyList<TriageSpot>? spots, TriageCandidate c) =>
        (spots ?? []).Where(s =>
            Math.Sqrt(((s.X - c.X) * (s.X - c.X)) + ((s.Y - c.Y) * (s.Y - c.Y))) <= Math.Max(s.RadiusPx, c.RadiusPx));

    /// <summary>True when the candidate sits on carried holds and every one of them is of a clearly different size.</summary>
    private static bool OtherSizeThanCarried(IReadOnlyList<TriageSpot>? carried, TriageCandidate c)
    {
        var under = OnSpot(carried, c).Where(s => s.RadiusPx > 0).ToList();
        return c.RadiusPx > 0 && under.Count > 0 && under.All(s =>
            Math.Max(s.RadiusPx, c.RadiusPx) > SameHoldSizeFactor * Math.Min(s.RadiusPx, c.RadiusPx));
    }

    private static NewHoldDiscardReason? Discard3D(IReadOnlyDictionary<Guid, Evidence3DVerdict>? evidence, Guid id) =>
        evidence?.GetValueOrDefault(id) switch
        {
            Evidence3DVerdict.KnownHold => NewHoldDiscardReason.KnownHoldIn3D,
            Evidence3DVerdict.OffWall => NewHoldDiscardReason.OffWallIn3D,
            _ => null,
        };

    private static void AddUnchanged(
        Dictionary<Guid, NewHoldDiscardReason> result,
        List<(Guid Id, PresenceQuery Query)> queries,
        Func<IReadOnlyList<PresenceQuery>, IReadOnlyList<double?>>? presence)
    {
        if (presence is null || queries.Count == 0)
        {
            return;
        }

        var scores = presence(queries.Select(q => q.Query).ToList());
        for (var i = 0; i < queries.Count && i < scores.Count; i++)
        {
            if (scores[i] >= SameAsOldScore)
            {
                result[queries[i].Id] = NewHoldDiscardReason.UnchangedSinceOldPhoto;
            }
        }
    }
}
