// <copyright file="WallBigUpdateService.TriageFootprints.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

/// <summary>
/// The footprints the new-hold triage compares a panel's unpaired detections with, in RAW pixels of the staged photo: each
/// detection's own outline, and every carried hold's — the detection an old hold is carried onto, or for an old hold no
/// detection carries, its outline (or circle) where the matcher's warp puts it.
/// </summary>
public partial class WallBigUpdateService
{
    /// <summary>Fewest found-again pairs with colours for a white-balance difference to be estimated.</summary>
    private const int MinColourPairs = 10;

    private static TriageCandidate Candidate(Hold h, (int Width, int Height) size) => new(
        h.Id,
        h.X * size.Width,
        h.Y * size.Height,
        h.Radius * Math.Max(size.Width, size.Height),
        NewHoldTriage.IsConfident(h.Confidence, h.ShapePoints is { Count: >= 3 }, h.OutlineConfidence),
        Outline(h, size),
        Lab(h));

    private static List<TriageOutline> CarriedOutlines(
        IReadOnlyList<(Hold Old, Hold New)> twins, IReadOnlyList<WarpedOldHold> notFound, (int Width, int Height) size)
    {
        var longSide = Math.Max(size.Width, size.Height);
        var outlines = twins
            .Select(t => new TriageOutline(t.New.X * size.Width, t.New.Y * size.Height, t.New.Radius * longSide, Outline(t.New, size), Lab(t.New)))
            .ToList();
        outlines.AddRange(notFound.Where(n => n.StillThere).Select(n => new TriageOutline(
            n.At.X * size.Width,
            n.At.Y * size.Height,
            n.Radius * longSide,
            n.Shape is { Count: >= 3 } shape ? shape.Select(v => (v.X * size.Width, v.Y * size.Height)).ToList() : null,
            n.Lab,
            n.Id)));
        return outlines;
    }

    /// <summary>Whether an old hold no detection carries still counts as a hold on its spot (see <see cref="WarpedOldHold.StillThere"/>).</summary>
    private static bool Carries(Hold old, RemovalInputs removals) =>
        !old.IsVirtual && removals.NoLongerShown?.Contains(old.Id) != true;

    /// <summary>
    /// The white-balance difference of the two captures: the median colour change (new minus old) over the found-again pairs
    /// whose both holds have a fingerprint, or null with fewer than <see cref="MinColourPairs"/> of them.
    /// </summary>
    private static TriageLab? ColourShift(IReadOnlyList<(Hold Old, Hold New)> twins)
    {
        var deltas = twins
            .Select(t => (Old: Lab(t.Old), New: Lab(t.New)))
            .Where(p => p is { Old: not null, New: not null })
            .Select(p => (L: p.New!.Value.L - p.Old!.Value.L, A: p.New.Value.A - p.Old.Value.A, B: p.New.Value.B - p.Old.Value.B))
            .ToList();
        return deltas.Count < MinColourPairs ? null : new TriageLab(Median(deltas.Select(d => d.L)), Median(deltas.Select(d => d.A)), Median(deltas.Select(d => d.B)));
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted[sorted.Count / 2];
    }

    /// <summary>The hold's mean colour from its fingerprint, or null when it has none.</summary>
    private static TriageLab? Lab(Hold h) =>
        HoldFingerprint.FromJson(h.FingerprintJson) is { } f ? new TriageLab(f.L, f.A, f.B) : null;

    /// <summary>
    /// A detection kept because its colour says another hold now sits on the spot of an old hold no detection carries: that
    /// old hold is recorded as replaced, so the confirm screen asks about it and its boulders are marked, as for a replacement
    /// the look check found. A swap inside a found-again hold only keeps the detection.
    /// </summary>
    private static void RecordSwaps(NewHoldTriageOutcome triage, IReadOnlySet<Guid> notCarriedOnto, LookOutcome looks)
    {
        foreach (var (swap, panelId) in triage.Swaps)
        {
            if (swap.OldHoldId is { } old && notCarriedOnto.Contains(old) && looks.Findings.All(f => f.OldHoldId != old))
            {
                looks.NotFoundAgain[old] = HoldMoveOutcome.Replaced;
                looks.Findings.Add(new HoldLookFinding(old, swap.DetectionId, panelId, HoldLook.Replaced, null));
            }
        }
    }

    private static List<(double X, double Y)>? Outline(Hold h, (int Width, int Height) size) =>
        h.ShapePoints is { Count: >= 3 } points
            ? points.Select(p => ((h.X + p.Dx) * size.Width, (h.Y + p.Dy) * size.Height)).ToList()
            : null;
}
