// <copyright file="NewHoldTriageAtticTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Xunit.Abstractions;

namespace Blocwerk.Core.Tests.Triage;

/// <summary>
/// Regression on real data: The Attic's panel update (centre + an oblique right panel). The default triage
/// must keep every real new hold and discard at least 90 % of the junk on each panel — the centre is
/// triaged first, and the right panel sees the centre through the overlap pairs.
/// </summary>
public class NewHoldTriageAtticTests(ITestOutputHelper output)
{
    private const double MinimumJunkDiscarded = 0.9;

    [Fact]
    public void Attic_KeepsEveryRealNewHold_AndDiscardsTheJunk()
    {
        var fixture = AtticTriageFixture.Load();
        var main = fixture["main"];
        var right = fixture["right"];

        var mainResult = Classify(main, null);
        var mainKept = main.Candidates.Where(c => !mainResult.ContainsKey(c.Id)).Select(c => (c.X, c.Y)).ToList();
        var rightResult = Classify(right, new OverlapOwner(right.OverlapToMain, main.Size, mainKept, right.Size));

        AssertPanel("main", main, mainResult);
        AssertPanel("right", right, rightResult);
    }

    [Fact]
    public void Attic_WithoutTheCentre_TheRightPanelKeepsMostOfTheMainWall()
    {
        var right = AtticTriageFixture.Load()["right"];

        var result = Classify(right, null);

        Assert.DoesNotContain(result.Values, r => r == NewHoldDiscardReason.SeenOnNeighbourPanel);
        Assert.True(Rate(right, result) < MinimumJunkDiscarded);
    }

    private static Dictionary<Guid, NewHoldDiscardReason> Classify(AtticPanel panel, OverlapOwner? owner)
    {
        var input = new NewHoldTriageInput(
            panel.Candidates.Select(c => new TriageCandidate(c.Id, c.X, c.Y)).ToList(),
            panel.Pairs,
            panel.OldSize,
            panel.Markers,
            owner);
        var scores = panel.Candidates.ToDictionary(c => (c.X, c.Y), c => c.Presence);
        return NewHoldTriage.Classify(input, q => Presence(scores, q));
    }

    // The fixture carries the probe's score per detection (computed on the photos); look it up by position.
    private static IReadOnlyList<double?> Presence(
        IReadOnlyDictionary<(double, double), double?> scores, IReadOnlyList<PresenceQuery> queries) =>
        queries.Select(q => scores.GetValueOrDefault((q.NewX, q.NewY))).ToList();

    private static double Rate(AtticPanel panel, IReadOnlyDictionary<Guid, NewHoldDiscardReason> result)
    {
        var junk = panel.Candidates.Where(c => !c.Real).ToList();
        return (double)junk.Count(c => result.ContainsKey(c.Id)) / junk.Count;
    }

    private void AssertPanel(string name, AtticPanel panel, IReadOnlyDictionary<Guid, NewHoldDiscardReason> result)
    {
        var real = panel.Candidates.Where(c => c.Real).ToList();
        var junk = panel.Candidates.Count - real.Count;
        var discarded = panel.Candidates.Count(c => !c.Real && result.ContainsKey(c.Id));
        output.WriteLine(
            $"{name}: kept {real.Count(c => !result.ContainsKey(c.Id))}/{real.Count} real, discarded {discarded}/{junk} junk "
            + $"({string.Join(", ", result.GroupBy(r => r.Value).Select(g => $"{g.Key} {g.Count()}"))})");

        Assert.All(real, c => Assert.False(result.ContainsKey(c.Id), $"real new hold at ({c.X}, {c.Y}) discarded"));
        Assert.True(Rate(panel, result) >= MinimumJunkDiscarded, $"{name}: only {discarded}/{junk} junk discarded");
    }
}
