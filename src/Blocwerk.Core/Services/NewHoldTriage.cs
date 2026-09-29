// <copyright file="NewHoldTriage.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

/// <summary>An unpaired staged detection, in RAW pixels of the staged photo.</summary>
public readonly record struct TriageCandidate(Guid Id, double X, double Y);

/// <summary>What the triage of one staged panel knows (all RAW pixels).</summary>
/// <param name="Candidates">The unpaired auto-detected staged holds.</param>
/// <param name="NewToOld">Matched pairs, staged photo → old photo. Empty when the panel was not aligned.</param>
/// <param name="OldSize">The old photo's size, or null when there is none.</param>
/// <param name="MarkerQuads">Printed-marker corners detected on the staged photo.</param>
/// <param name="Owner">For a neighbour panel: the centre panel that owns the overlap with it. Null for the centre.</param>
public sealed record NewHoldTriageInput(
    IReadOnlyList<TriageCandidate> Candidates,
    IReadOnlyList<PointPair> NewToOld,
    (int Width, int Height)? OldSize,
    IReadOnlyList<IReadOnlyList<(double X, double Y)>> MarkerQuads,
    OverlapOwner? Owner = null);

/// <summary>
/// Picks the unpaired staged detections that are most likely NOT new holds, so the review discards them
/// by default (the user can still keep any of them). Conservative on purpose: a detection is only
/// suggested when it sits on a printed marker, when (on a neighbour panel) the centre photo shows that spot,
/// when the aligned old photo did not cover it, or when the old photo shows the same thing at the aligned spot.
/// </summary>
public static class NewHoldTriage
{
    /// <summary>How far (fraction of the old photo) outside its frame a spot must map to count as uncovered.</summary>
    public const double OutsideMargin = 0;

    /// <summary>Correlation at or above which the old photo already showed the same thing.</summary>
    public const double SameAsOldScore = 0.6;

    /// <summary>The marker quad grown by this factor also covers its white print border.</summary>
    public const double MarkerGrowth = 1.35;

    private const int MinimumPairs = 6;

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

            var scale = LocalAffine.Scale(input.NewToOld, c.X, c.Y) ?? 1;
            queries.Add((c.Id, new PresenceQuery(c.X, c.Y, old.X, old.Y, scale)));
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
