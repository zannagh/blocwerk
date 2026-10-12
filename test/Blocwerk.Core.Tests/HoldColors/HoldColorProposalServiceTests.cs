// <copyright file="HoldColorProposalServiceTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Holds.Colors;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests.HoldColors;

/// <summary>
/// "Suggest colours for holds without one": live holds without a colour get the colour their panel photo shows, holds
/// with a colour and holds of other walls or older generations are never touched, and a second run changes nothing.
/// </summary>
public class HoldColorProposalServiceTests
{
    [Fact]
    public async Task HoldsWithoutAColour_GetAProposal_AndEverythingElseStaysAsItWas()
    {
        using var s = await HoldColorScenario.CreateAsync();
        var panel = await s.AddPanelAsync(0);
        var blue = await s.AddHoldAsync(panel, HoldColorScenario.BlueSpot);
        var setByHand = await s.AddHoldAsync(panel, HoldColorScenario.RedSpot, configure: h => h.Color = "purple");
        var onTheWall = await s.AddHoldAsync(panel, HoldColorScenario.WallSpot);
        var virtualHold = await s.AddHoldAsync(panel, HoldColorScenario.GreenSpot, configure: h => h.IsVirtual = true);

        var report = await s.Service().SuggestMissingAsync(s.WallId);

        Assert.Equal((2, 1, 0, 1), (report.Considered, report.Proposed, report.FromLinked, report.LeftUnset));
        Assert.Equal(1, report.ProposedByColor["blue"]);
        Assert.Equal(1, report.UnsetByVerdict[HoldColorVerdict.LikeSurroundings]);
        var proposed = await s.GetAsync(blue);
        Assert.Equal(("blue", "blue", true), (proposed.Color, proposed.ProposedColor, proposed.HasProposedColor));
        var curated = await s.GetAsync(setByHand);
        Assert.Equal(("purple", null, false), (curated.Color, curated.ProposedColor, curated.HasProposedColor));
        Assert.Null((await s.GetAsync(onTheWall)).Color);
        Assert.Null((await s.GetAsync(virtualHold)).Color);
    }

    [Fact]
    public async Task ASecondRun_ChangesNothing()
    {
        using var s = await HoldColorScenario.CreateAsync();
        var panel = await s.AddPanelAsync(0);
        var blue = await s.AddHoldAsync(panel, HoldColorScenario.BlueSpot);
        var onTheWall = await s.AddHoldAsync(panel, HoldColorScenario.WallSpot);
        await s.Service().SuggestMissingAsync(s.WallId);

        // Between the runs somebody corrects the proposal: the next run must leave that alone as well.
        await s.SetAsync(blue, h => h.Color = "purple");
        var again = await s.Service().SuggestMissingAsync(s.WallId);

        Assert.False(again.Any);
        Assert.Equal((1, 0, 1), (again.Considered, again.Proposed, again.LeftUnset));
        Assert.Equal("purple", (await s.GetAsync(blue)).Color);
        Assert.False((await s.GetAsync(blue)).HasProposedColor);
        Assert.Null((await s.GetAsync(onTheWall)).Color);
    }

    [Fact]
    public async Task OnlyTheWallsLiveHolds_AreLookedAt()
    {
        using var s = await HoldColorScenario.CreateAsync();
        var oldPanel = await s.AddPanelAsync(0, generation: 0);
        var historic = await s.AddHoldAsync(oldPanel, HoldColorScenario.BlueSpot);
        var livePanel = await s.AddPanelAsync(0, generation: 1);
        var live = await s.AddHoldAsync(livePanel, HoldColorScenario.RedSpot, generation: 1);
        var stagedPanel = await s.AddPanelAsync(0, generation: 2, staged: true);
        var staged = await s.AddHoldAsync(stagedPanel, HoldColorScenario.GreenSpot, generation: 2);
        var (otherWall, otherHold) = await AddOtherWallAsync(s);
        await using (var db = s.Harness.CreateContext())
        {
            (await db.Walls.FirstAsync(w => w.Id == s.WallId)).CurrentGeneration = 1;
            await db.SaveChangesAsync();
        }

        var report = await s.Service().SuggestMissingAsync(s.WallId);

        Assert.Equal((1, 1), (report.Considered, report.Proposed));
        Assert.Equal("red", (await s.GetAsync(live)).Color);
        Assert.Null((await s.GetAsync(historic)).Color);
        Assert.Null((await s.GetAsync(staged)).Color);
        Assert.Null((await s.GetAsync(otherHold)).Color);
        Assert.NotEqual(s.WallId, otherWall);
    }

    [Fact]
    public async Task ALinkedHold_GetsOneColour_FromTheSureView_OrFromTheColourItsTwinHas()
    {
        using var s = await HoldColorScenario.CreateAsync();
        var centre = await s.AddPanelAsync(0);
        var right = await s.AddPanelAsync(1, photo: HoldColorScenario.Photo(greenSpot: HoldColorTestPhoto.Wood));

        // Sure on the centre panel, hidden on the right one.
        var seen = await s.AddHoldAsync(centre, HoldColorScenario.GreenSpot);
        var hidden = await s.AddHoldAsync(right, HoldColorScenario.GreenSpot);
        await s.LinkAsync(seen, hidden);

        // The twin on the centre panel was set by hand: the right one takes that, not what its photo shows.
        var setByHand = await s.AddHoldAsync(centre, HoldColorScenario.RedSpot, configure: h => h.Color = "pink");
        var twin = await s.AddHoldAsync(right, HoldColorScenario.RedSpot);
        await s.LinkAsync(setByHand, twin);

        var report = await s.Service().SuggestMissingAsync(s.WallId);

        Assert.Equal((2, 1, 0), (report.Proposed, report.FromLinked, report.LeftUnset));
        Assert.Equal(("green", "green"), ((await s.GetAsync(seen)).Color, (await s.GetAsync(hidden)).Color));
        Assert.True((await s.GetAsync(hidden)).HasProposedColor);
        var filled = await s.GetAsync(twin);
        Assert.Equal(("pink", false), (filled.Color, filled.HasProposedColor));
        Assert.Equal("pink", (await s.GetAsync(setByHand)).Color);
    }

    [Fact]
    public async Task TwoViewsThatDisagree_LeaveTheHoldUnset()
    {
        using var s = await HoldColorScenario.CreateAsync();
        var centre = await s.AddPanelAsync(0);
        var right = await s.AddPanelAsync(1, photo: HoldColorScenario.Photo(blueSpot: HoldColorTestPhoto.Yellow));
        var a = await s.AddHoldAsync(centre, HoldColorScenario.BlueSpot);
        var b = await s.AddHoldAsync(right, HoldColorScenario.BlueSpot);
        await s.LinkAsync(a, b);

        var report = await s.Service().SuggestMissingAsync(s.WallId);

        Assert.Equal(2, report.UnsetByVerdict[HoldColorVerdict.ViewsDisagree]);
        Assert.Null((await s.GetAsync(a)).Color);
        Assert.Null((await s.GetAsync(b)).Color);
    }

    [Fact]
    public async Task AnUnreadablePhoto_LeavesItsHoldsUnset_AndTheStatusCounts()
    {
        using var s = await HoldColorScenario.CreateAsync();
        var broken = await s.AddPanelAsync(0, photo: [1, 2, 3]);
        var hold = await s.AddHoldAsync(broken, HoldColorScenario.BlueSpot);
        await s.AddHoldAsync(broken, HoldColorScenario.RedSpot, configure: h => (h.Color, h.ProposedColor) = ("red", "red"));

        var report = await s.Service().SuggestMissingAsync(s.WallId);
        var status = await s.Service().GetStatusAsync(s.WallId);

        Assert.Equal(1, report.UnsetByVerdict[HoldColorVerdict.NoPhoto]);
        Assert.Null((await s.GetAsync(hold)).Color);
        Assert.Equal(new HoldColorProposalStatus(1, 1), status);
    }

    [Fact]
    public async Task OnlyAWallAdmin_MayRunIt()
    {
        using var s = await HoldColorScenario.CreateAsync();
        var panel = await s.AddPanelAsync(0);
        var hold = await s.AddHoldAsync(panel, HoldColorScenario.BlueSpot);
        s.Harness.ActingUser = await s.Harness.AddMemberAsync("member@test", WallRole.Member);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Service().SuggestMissingAsync(s.WallId));

        Assert.Null((await s.GetAsync(hold)).Color);
    }

    private static async Task<(Guid WallId, Guid HoldId)> AddOtherWallAsync(HoldColorScenario s)
    {
        Guid wallId;
        await using (var db = s.Harness.CreateContext())
        {
            var wall = new Wall { Name = "Other wall", OwnerId = s.Harness.Owner.Id, Photo = [1, 2, 3], PhotoContentType = "image/jpeg" };
            db.Walls.Add(wall);
            await db.SaveChangesAsync();
            wallId = wall.Id;
        }

        var panel = await s.AddPanelAsync(0, wallId: wallId);
        return (wallId, await s.AddHoldAsync(panel, HoldColorScenario.BlueSpot, wallId: wallId));
    }
}
