// <copyright file="UpdateOveraddAtticTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Enums;
using Blocwerk.Core.HoldMoves;
using Blocwerk.Core.Services;
using Xunit.Abstractions;

namespace Blocwerk.Core.Tests.Triage;

/// <summary>
/// Regression on real data: The Attic's quick update of 2026-10-19 added every unpaired detection the triage kept as a new
/// hold, 113 of them, of which the verifier found 26 real (+3 unsure): 34 were noise and 12 a second outline of a carried
/// hold. Its neighbour rule threw away two real new holds on the right side wall, and 40 holds were reported moved while
/// none moved. Here the triage runs again over the staged geometry (centre first, then the right panel against it) and is
/// held against the verifier's labels; and the 40 "moved" pairs are judged again.
/// </summary>
public class UpdateOveraddAtticTests(ITestOutputHelper output)
{
    private static readonly HashSet<string> Real = ["realNew", "realNewUnsure", "realDiscarded"];
    private static readonly HashSet<string> Existing = ["existingPreviouslyUndetected", "existingPreviouslyUndetectedUnsure"];
    private static readonly HashSet<string> Noise = ["noiseNonHold"];
    private static readonly HashSet<string> Duplicates = ["duplicateOfLiveHold", "neighbourPanelDuplicate", "duplicateResolvedByMerge"];

    [Fact]
    public void Attic_Triage_DropsTheDuplicatesAndNoise_AndKeepsTheRealNewHolds()
    {
        var (panels, _) = UpdateOveraddFixture.Load();
        var all = panels["centre"].Candidates.Concat(panels["right"].Candidates).ToList();
        var before = all.Where(c => c.Before is not null).ToDictionary(c => c.Id, c => c.Before!);
        var emulated = Triage(panels, newRules: false);
        var after = Triage(panels, newRules: true);

        Report(all, before, "before (the real run)");
        Report(all, emulated.ToDictionary(a => a.Key, a => a.Value.ToString()), "before (old rules, emulated on the fixture)");
        Report(all, after.ToDictionary(a => a.Key, a => a.Value.ToString()), "after");
        foreach (var c in all.Where(c => Real.Contains(c.Label) && after.ContainsKey(c.Id)))
        {
            output.WriteLine($"real hold discarded: {c.Id.ToString()[..8]} ({c.Label}) as {after[c.Id]}, before: {c.Before ?? "kept"}");
        }

        // The emulation (overlap pairs measured without the 3D model's anchors) is the baseline the rules are held against.
        Assert.Equal(118, Kept(all, before, _ => true));

        // The measured result, no slack: any change of these numbers is a change of behaviour to look at.
        Assert.Equal((123, 23, 26, 3), (Kept(all, emulated, _ => true), Kept(all, emulated, Noise.Contains), Kept(all, emulated, Duplicates.Contains), Discarded(all, emulated, Real.Contains)));
        Assert.True(Kept(all, after, _ => true) <= 101, "new holds proposed");
        Assert.True(Kept(all, after, Existing.Contains) >= 18, "existing holds that had no outline kept");
        Assert.True(Kept(all, after, Noise.Contains) <= 18, "noise kept");
        Assert.True(Kept(all, after, Duplicates.Contains) <= 14, "duplicates kept");
        Assert.True(Discarded(all, after, Real.Contains) <= 2, "real new holds discarded");
        Assert.Equal(NewHoldDiscardReason.OnMarker, after[Id(all, "c91c1d44")]);
        Assert.Equal(NewHoldDiscardReason.SeenOnNeighbourPanel, emulated[Id(all, "735d9467")]);
        Assert.False(after.ContainsKey(Id(all, "735d9467")), "the black/white hold on the right-panel beam");
        Assert.False(after.ContainsKey(Id(all, "e5e69ffa")), "the lilac ball on the right side wall");
        Assert.False(after.ContainsKey(Id(all, "f39cd15c")), "N57, the purple hold that replaced the orange one");

        // A confident right-panel detection on a spot the centre settled only by discarding its own detection there stays: else
        // the hold is on neither photo (exp, 2026-10-20: the beige 3f1b772e, dropped after the 3D re-check).
        Assert.False(after.ContainsKey(Id(all, "47f13dbb")), "the beige hold at the right panel's edge");

        // Two real right-panel holds the real run kept are discarded here under the old rules as well, so not by these
        // changes: with the harness's overlap pairs (no 3D anchors) f9164ac2 maps 30 px onto a carried centre hold of about its
        // size, and 0c55a6a9 67 px from the centre's own kept twin (the twin distance is 60 px). Widening that distance by the
        // detection's radius kept the latter but let 6 more noise detections through, so the rule stays.
        Assert.Equal(NewHoldDiscardReason.SeenOnNeighbourPanel, emulated[Id(all, "f9164ac2")]);
        Assert.Equal(NewHoldDiscardReason.SeenOnNeighbourPanel, emulated[Id(all, "0c55a6a9")]);
    }

    [Fact]
    public void Attic_Moves_FewerFalseMoves_AndFewerBouldersMarked()
    {
        var (_, moves) = UpdateOveraddFixture.Load();

        var stillMoved = moves
            .Where(m => !m.DeclinedByMatcher && !ReOutlineRule.Explains(
                NewHoldTriageGeometry.Area(m.Old), m.Old.Points is not null, NewHoldTriageGeometry.Area(m.Twin), m.Twin.Points is not null,
                m.Mm, m.RotationDeg, new HoldMoveOptions()))
            .ToList();

        output.WriteLine(
            $"moved: before {moves.Count} (0 real), after {stillMoved.Count} ({string.Join(", ", stillMoved.Select(m => $"M{m.M} {m.Label}"))}); "
            + $"declined by the matcher {moves.Count(m => m.DeclinedByMatcher)}, explained as re-outlines "
            + $"{moves.Count(m => !m.DeclinedByMatcher) - stillMoved.Count}; boulders marked before {moves.Count(m => m.Boulders > 0)}, "
            + $"after {stillMoved.Count(m => m.Boulders > 0)}");
        Assert.Equal(40, moves.Count);

        // The re-outline rule only explains a shift when the outline changed, the hold did not turn and the shift is small, so
        // most of these false moves stay "moved, kept" (a review mark, never a silent change); the matcher declines 6 jumps.
        Assert.True(stillMoved.Count <= 27, $"{stillMoved.Count} false moves left");
        Assert.True(stillMoved.Count(m => m.Boulders > 0) <= 2, "boulders marked");
    }

    /// <summary>Triages the centre, then the right panel against it, with the new rules or (as a baseline) without them.</summary>
    internal static Dictionary<Guid, NewHoldDiscardReason> Triage(IReadOnlyDictionary<string, OveraddPanel> panels, bool newRules)
    {
        var (centre, right) = (panels["centre"], panels["right"]);
        var result = Classify(centre, null, newRules);
        foreach (var (id, reason) in Classify(right, Owner(centre, result, right, newRules), newRules))
        {
            result[id] = reason;
        }

        return result;
    }

    /// <summary>One panel's triage. As the service, an old hold the look check found gone or replaced carries no second outline.</summary>
    internal static Dictionary<Guid, NewHoldDiscardReason> Classify(OveraddPanel panel, OverlapOwner? owner, bool newRules)
    {
        var input = new NewHoldTriageInput(
            panel.Candidates.Select(c => new TriageCandidate(
                c.Id, c.X, c.Y, c.RadiusPx, NewHoldTriage.IsConfident(c.Confidence, c.Outline is not null, c.OutlineConfidence), c.Outline, c.Lab)).ToList(),
            panel.Pairs,
            panel.OldSize,
            panel.Markers,
            owner,
            Carried: panel.Carriers.Where(k => !k.Kept).Select(k => new TriageSpot(k.Outline.X, k.Outline.Y, k.Outline.RadiusPx)).ToList(),
            NotFound: panel.Carriers.Where(k => k.Kept).Select(k => new TriageSpot(k.Outline.X, k.Outline.Y, k.Outline.RadiusPx)).ToList(),
            CarriedOutlines: newRules ? panel.Carriers.Where(k => !k.Gone).Select(k => k.Outline).ToList() : null,
            OldMarkerQuads: newRules ? panel.OldMarkers : null,
            OldToNewColourShift: newRules ? panel.ColourShift : null);

        // The swap check asks with the detection's own radius; the fixture has both scores.
        var scores = panel.Candidates.ToDictionary(c => (c.X, c.Y), c => (c.Presence, c.PresenceOwn, c.RadiusPx));
        double? Score(PresenceQuery q) =>
            scores.TryGetValue((q.NewX, q.NewY), out var s) ? (q.RadiusPx == s.RadiusPx && s.RadiusPx > 0 ? s.PresenceOwn : s.Presence) : null;
        var result = NewHoldTriage.Classify(input, q => q.Select(Score).ToList());

        // The run's lenient marker pass read the photo itself (dark and oblique markers); the fixture has no photos.
        foreach (var c in panel.Candidates.Where(c => c.Before == nameof(NewHoldDiscardReason.OnMarker)))
        {
            result[c.Id] = NewHoldDiscardReason.OnMarker;
        }

        return result;
    }

    /// <summary>The right panel's owner, as the service builds it: the measured overlap pairs and what the centre kept and settled.</summary>
    internal static OverlapOwner Owner(
        OveraddPanel centre, IReadOnlyDictionary<Guid, NewHoldDiscardReason> centreResult, OveraddPanel right, bool newRules)
    {
        var kept = centre.Candidates.Where(c => !centreResult.ContainsKey(c.Id)).Select(c => (c.X, c.Y)).ToList();
        var discarded = centre.Candidates.Where(c => centreResult.ContainsKey(c.Id)).Select(c => new TriageSpot(c.X, c.Y, c.RadiusPx)).ToList();
        var measured = right.OverlapToMain.Where(p => NeighbourPanelRule.IsMeasured(p.Confidence)).Select(p => p.Pair).ToList();
        var pairs = right.OverlapToMain.Select(p => p.Pair).ToList();

        // The old rules counted the centre's own discards as its holds for every detection; the new ones only for an unsure one.
        return newRules
            ? new OverlapOwner(pairs, (4032, 3024), kept, (4032, 3024), centre.Settled, measured, discarded)
            : new OverlapOwner(pairs, (4032, 3024), kept, (4032, 3024), [.. centre.Settled, .. discarded]);
    }

    private static int Kept<T>(IEnumerable<OveraddCandidate> all, IReadOnlyDictionary<Guid, T> discards, Func<string, bool> label) =>
        all.Count(c => label(c.Label) && !discards.ContainsKey(c.Id));

    private static int Discarded<T>(IEnumerable<OveraddCandidate> all, IReadOnlyDictionary<Guid, T> discards, Func<string, bool> label) =>
        all.Count(c => label(c.Label) && discards.ContainsKey(c.Id));

    private static Guid Id(IEnumerable<OveraddCandidate> all, string prefix) => all.Single(c => c.Id.ToString().StartsWith(prefix, StringComparison.Ordinal)).Id;

    private void Report(IReadOnlyList<OveraddCandidate> all, IReadOnlyDictionary<Guid, string> discards, string name)
    {
        string Line(string label, HashSet<string> set) =>
            $"{label} kept {Kept(all, discards, set.Contains)}/{all.Count(c => set.Contains(c.Label))}";
        output.WriteLine(
            $"{name}: new holds proposed {Kept(all, discards, _ => true)} of {all.Count} unpaired; {Line("real", Real)}, {Line("existing", Existing)}, "
            + $"{Line("noise", Noise)}, {Line("duplicates", Duplicates)} | "
            + string.Join(", ", discards.Values.GroupBy(v => v).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}")));
    }
}
