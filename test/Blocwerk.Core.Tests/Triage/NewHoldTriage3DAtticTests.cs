// <copyright file="NewHoldTriage3DAtticTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests.Triage;

/// <summary>
/// On The Attic's fixture, 3D verdicts only ever add discards to the photo-only triage — on the centre, and on the right
/// panel when it sees the centre as that same triage kept it (fewer centre holds kept means more right-panel discards).
/// </summary>
public class NewHoldTriage3DAtticTests
{
    [Theory]
    [InlineData(3)]
    [InlineData(7)]
    public void Verdicts3D_OnlyAddDiscards_OnBothPanels(int every)
    {
        var fixture = AtticTriageFixture.Load();
        var (main, right) = (fixture["main"], fixture["right"]);

        var mainPhoto = Classify(main, null, null);
        var main3D = Classify(main, null, Verdicts(main, every));
        var rightPhoto = Classify(right, Owner(right, main, mainPhoto), null);
        var right3D = Classify(right, Owner(right, main, main3D), Verdicts(right, every));

        Assert.All(mainPhoto.Keys, id => Assert.True(main3D.ContainsKey(id)));
        Assert.All(rightPhoto.Keys, id => Assert.True(right3D.ContainsKey(id)));
    }

    /// <summary>Every <paramref name="every"/>-th candidate a known hold, the next one off the wall, the next one seen in 3D.</summary>
    private static Dictionary<Guid, Evidence3DVerdict> Verdicts(AtticPanel panel, int every) =>
        panel.Candidates.Select((c, i) => (c.Id, V: (i % every) switch
        {
            0 => Evidence3DVerdict.KnownHold,
            1 => Evidence3DVerdict.OffWall,
            2 => Evidence3DVerdict.SeenIn3D,
            _ => Evidence3DVerdict.None,
        })).ToDictionary(x => x.Id, x => x.V);

    private static OverlapOwner Owner(AtticPanel right, AtticPanel main, IReadOnlyDictionary<Guid, NewHoldDiscardReason> mainResult) =>
        new(right.OverlapToMain, main.Size, main.Candidates.Where(c => !mainResult.ContainsKey(c.Id)).Select(c => (c.X, c.Y)).ToList(), right.Size);

    private static Dictionary<Guid, NewHoldDiscardReason> Classify(
        AtticPanel panel, OverlapOwner? owner, IReadOnlyDictionary<Guid, Evidence3DVerdict>? verdicts)
    {
        var input = new NewHoldTriageInput(
            panel.Candidates.Select(c => new TriageCandidate(c.Id, c.X, c.Y)).ToList(), panel.Pairs, panel.OldSize, panel.Markers, owner, verdicts);
        var scores = panel.Candidates.ToDictionary(c => (c.X, c.Y), c => c.Presence);
        return NewHoldTriage.Classify(input, q => q.Select(x => scores.GetValueOrDefault((x.NewX, x.NewY))).ToList());
    }
}
