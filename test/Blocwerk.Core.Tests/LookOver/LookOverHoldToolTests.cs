// <copyright file="LookOverHoldToolTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using static Blocwerk.Core.Tests.LookOver.LookOverFixture;

namespace Blocwerk.Core.Tests.LookOver;

/// <summary>
/// The look-over reads the editor's merge and split tools right: a hold merged away by the merge tool is "merged in the
/// editor", not "removed", and the new part of a split is not a hold someone placed by hand.
/// </summary>
public class LookOverHoldToolTests
{
    [Fact]
    public async Task AKeptHoldMergedAwayWithTheMergeTool_ClosesItsFindingAsMergedInTheEditor()
    {
        using var h = new WallTestHarness();
        var k = await KeptHoldFixture.ApplyAsync(h);
        var old = (await HoldAsync(h, k.UnusedId))!;
        var neighbour = await KeptHoldFixture.AddByHandAsync(h, k.Wall.WallId, old.WallPanelId!.Value, old.X + 0.02, old.Y, old.Radius);

        await HoldToolSeed.Tools(h).MergeAsync(k.Wall.WallId, neighbour.Id, k.UnusedId);

        var view = (await Service(h).ListAsync(k.Wall.WallId)).Single(v => v.OldHoldId == k.UnusedOldId);
        Assert.Equal(
            (LookOverFindingStatus.Closed, (LookOverCloseReason?)LookOverCloseReason.MergedInEditor),
            (view.Status, view.ClosedReason));
    }

    [Fact]
    public async Task TheNewPartOfASplit_OnTheSpotOfALeftOutDetection_DoesNotCloseItAsAHoldAddedThere()
    {
        using var h = new WallTestHarness();
        var k = await KeptHoldFixture.ApplyAsync(h);
        var panel = (await HoldAsync(h, k.UnusedId))!.WallPanelId!.Value;
        var leftOut = await AddFindingAsync(h, new WallLookOverFinding
        {
            WallId = k.Wall.WallId, Generation = 3, Kind = LookOverFindingKind.ConflictingNew, PanelId = panel, X = 0.44, Y = 0.3, Radius = 0.03,
        });
        var detected = await KeptHoldFixture.AddByHandAsync(h, k.Wall.WallId, panel, 0.40, 0.3, 0.05);
        await using (var db = h.CreateContext())
        {
            db.Holds.Single(x => x.Id == detected.Id).IsAutoDetected = true;
            await db.SaveChangesAsync();
        }

        var split = await HoldToolSeed.Tools(h).SplitAsync(k.Wall.WallId, detected.Id, [new NormalizedPoint(0.42, 0.2), new NormalizedPoint(0.42, 0.4)], []);

        var part = (await HoldAsync(h, split.OtherHoldId))!;
        Assert.InRange(part.X, 0.42, 0.47);
        var view = (await Service(h).ListAsync(k.Wall.WallId)).Single(v => v.Id == leftOut);
        Assert.Equal(LookOverFindingStatus.Open, view.Status);
    }
}
