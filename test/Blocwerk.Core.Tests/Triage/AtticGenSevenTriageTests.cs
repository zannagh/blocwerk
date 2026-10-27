// <copyright file="AtticGenSevenTriageTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Xunit.Abstractions;

namespace Blocwerk.Core.Tests.Triage;

/// <summary>
/// Regression on real data: The Attic's gen 6 → 7 update proposed 71 new holds, of which 25 were really new and 5 existing
/// holds never outlined; the other 41 were noise: 16 parts of carried holds, 8 printed markers, 10 mats, beams and the
/// electrics box of the uncropped photo, 4 hangboard pockets and 3 dark corners. The triage runs again over the staged
/// geometry, with the run's rules and with the active model's markers and the outside-the-wall rule added, which must not
/// cost a real hold.
/// </summary>
public class AtticGenSevenTriageTests(ITestOutputHelper output)
{
    private static readonly HashSet<string> Kept = ["realNew", "existing"];

    [Fact]
    public void Gen7_ModelMarkersAndTheWall_DropMarkersAndMats_KeepEveryRealHold()
    {
        var (panels, modelJson) = AtticGenSevenFixture.Load();
        var markers = ModelMarkerProjection.Parse(modelJson);
        var all = panels.Values.SelectMany(p => p.Candidates).ToList();
        var before = new Dictionary<Guid, NewHoldDiscardReason>();
        var after = new Dictionary<Guid, NewHoldDiscardReason>();
        foreach (var panel in panels.Values)
        {
            // Before: the run's rules, i.e. no model markers and no outside-the-wall rule.
            foreach (var (id, reason) in NewHoldTriage.Classify(panel.Input, null).Where(d => d.Value != NewHoldDiscardReason.OutsideWall))
            {
                before[id] = reason;
            }

            var quads = ModelMarkerProjection.Project(markers, panel.Anchors, (4032, 3024));
            foreach (var (id, reason) in NewHoldTriage.Classify(panel.Input with { ModelMarkerQuads = quads }, null))
            {
                after[id] = reason;
            }
        }

        var added = after.Keys.Except(before.Keys).ToHashSet();
        foreach (var group in all.GroupBy(c => c.Label).OrderBy(g => g.Key))
        {
            output.WriteLine(
                $"{group.Key}: {group.Count()} proposed by the run; the emulated run rules drop {Labels(group, before)}; the new rules also drop "
                + Labels(group.Where(c => added.Contains(c.Candidate.Id)), after));
        }

        Assert.Equal(71, all.Count);
        Assert.Equal(30, all.Count(c => Kept.Contains(c.Label)));
        Assert.DoesNotContain(all, c => Kept.Contains(c.Label) && after.ContainsKey(c.Candidate.Id));

        // The emulation is not the run: the staged twins' outlines are the detector's re-run, so the inside-a-carried-hold rule
        // already drops four of the noise detections the run proposed. The measured result, no slack.
        Assert.Equal([13, 17, 19, 22], Indices(all, before.Keys));
        Assert.All(before.Values, r => Assert.Equal(NewHoldDiscardReason.InsideCarriedHold, r));

        // What the new rules add: four of the eight printed markers, the mat edge beside one, two mats beyond the wall.
        Assert.Equal([5, 7, 10, 11, 20, 31, 33], Indices(all, added));
        Assert.Equal([7, 11, 31, 33], Indices(all.Where(c => c.Label == "marker"), added));
        Assert.Equal(NewHoldDiscardReason.OutsideWall, after[Id(all, 5)]);
        Assert.Equal(NewHoldDiscardReason.OutsideWall, after[Id(all, 10)]);
        Assert.All([7, 11, 20, 31, 33], i => Assert.Equal(NewHoldDiscardReason.OnMarker, after[Id(all, i)]));
    }

    [Fact]
    public void Gen7_AConfidentDetectionBeyondTheWall_StaysProposed()
    {
        var (panels, _) = AtticGenSevenFixture.Load();
        var all = panels.Values.SelectMany(p => p.Candidates).ToList();

        var outside = panels.Values.SelectMany(p => NewHoldTriage.Classify(p.Input, null)).Where(d => d.Value == NewHoldDiscardReason.OutsideWall).ToList();

        // The two real new holds above the old top row (#59, #62) lie beyond the hull, and so do the e-box bracket, a pipe, a
        // dark corner and a marker the detector is sure of (#21, #25, #34, #37): a sure, outlined detection is never off the wall.
        Assert.Equal([5, 7, 10, 11], Indices(all, outside.Select(d => d.Key)));
        Assert.All([21, 25, 34, 37, 59, 62], i => Assert.True(all.Single(c => c.Index == i).Candidate.Confident));
    }

    private static List<int> Indices(IEnumerable<GenSevenCandidate> all, IEnumerable<Guid> ids)
    {
        var set = ids.ToHashSet();
        return all.Where(c => set.Contains(c.Candidate.Id)).Select(c => c.Index).Order().ToList();
    }

    private static string Labels(IEnumerable<GenSevenCandidate> candidates, Dictionary<Guid, NewHoldDiscardReason> result)
    {
        var dropped = candidates.Where(c => result.ContainsKey(c.Candidate.Id)).Select(c => $"#{c.Index} {result[c.Candidate.Id]}").ToList();
        return dropped.Count == 0 ? "none" : string.Join(", ", dropped);
    }

    private static Guid Id(IEnumerable<GenSevenCandidate> all, int index) => all.Single(c => c.Index == index).Candidate.Id;
}
