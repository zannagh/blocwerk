using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Cover for the MIXED-GENERATION live read after a subset (per-panel) promote. Re-shooting only the
/// centre bumps <see cref="Wall.CurrentGeneration"/> (2→3) but leaves the un-updated side panel and its
/// holds at the old generation (gen 2). The live hold read is a PER-PANEL fact — an un-updated panel's
/// holds must still be read at THAT panel's own generation, never the wall's bumped generation, or the
/// panel renders its photo with no overlay. Drives the real <see cref="WallBigUpdateService"/> promote
/// against the SQLite harness (mirroring <see cref="BigUpdateSubsetPromoteTests"/>), then reads back
/// through <see cref="WallPanelService"/> (per-panel) and <see cref="WallService"/> (wall-level).
/// </summary>
public class MixedGenerationLiveReadTests
{
    // The un-updated side panel's live read must return its old-generation hold, and the re-shot centre's
    // live read must return the new-generation hold — the regression the per-panel keying fixes.
    [Fact]
    public async Task AfterCentreOnlyPromote_LiveReadOfUntouchedPanel_ReturnsOldGenHolds_CentreReturnsNewGen()
    {
        using var h = new WallTestHarness();
        var w = await SeedTwoPanelWallAsync(h);
        var (centrePanelId, stagedHoldId) = await StageCentreUpdateAsync(h, w.WallId);

        await NewBigUpdateService(h).PromoteAsync(
            w.WallId, Confirm(new CarryoverDecision(w.CentreHoldId, CarryKind.Carried, stagedHoldId)));

        var panelService = NewPanelService(h);

        // UN-UPDATED RIGHT PANEL: live read still returns its gen-2 hold (NOT empty). This is the bug —
        // keying on wall.CurrentGeneration (now 3) would query gen 3 on a gen-2 panel and return nothing.
        var rightLive = await panelService.GetPanelHoldsAsync(w.WallId, w.RightPanelId, includeStaged: false);
        Assert.Single(rightLive);
        Assert.Equal(w.RightHoldId, rightLive[0].Id);

        // RE-SHOT CENTRE PANEL: live read returns the promoted gen-3 hold.
        var centreLive = await panelService.GetPanelHoldsAsync(w.WallId, centrePanelId, includeStaged: false);
        Assert.Single(centreLive);
        Assert.Equal(stagedHoldId, centreLive[0].Id);
    }

    // The wall-level live read (GetWallAsync) must return the live holds of BOTH panels after the subset
    // promote — spanning generations (centre gen 3 + right gen 2) — and never the superseded old centre row.
    [Fact]
    public async Task AfterCentreOnlyPromote_WallLevelRead_ReturnsHoldsFromBothPanels_ExcludingSuperseded()
    {
        using var h = new WallTestHarness();
        var w = await SeedTwoPanelWallAsync(h);
        var (_, stagedHoldId) = await StageCentreUpdateAsync(h, w.WallId);

        await NewBigUpdateService(h).PromoteAsync(
            w.WallId, Confirm(new CarryoverDecision(w.CentreHoldId, CarryKind.Carried, stagedHoldId)));

        var wall = await h.WallService.GetWallAsync(w.WallId);
        Assert.NotNull(wall);

        var holdIds = wall!.Holds.Select(x => x.Id).ToHashSet();

        // Both live panels contribute: the re-shot centre's gen-3 hold and the untouched right's gen-2 hold.
        Assert.Contains(stagedHoldId, holdIds);
        Assert.Contains(w.RightHoldId, holdIds);

        // The superseded old centre row (gen 2) is excluded — its hold is on the retired panel, not a live one.
        Assert.DoesNotContain(w.CentreHoldId, holdIds);
        Assert.Equal(2, wall.Holds.Count);
    }

    private static WallBigUpdateService NewBigUpdateService(WallTestHarness h) =>
        new(
            h.DbContextFactory,
            h.CurrentUser,
            h.HoldDetection,
            Substitute.For<IHoldOverlapMatcher>(),
            NullLogger<WallBigUpdateService>.Instance);

    private static WallPanelService NewPanelService(WallTestHarness h) =>
        new(
            h.DbContextFactory,
            h.CurrentUser,
            h.HoldDetection,
            Substitute.For<IHoldOverlapMatcher>(),
            NullLogger<WallPanelService>.Instance);

    private static BigUpdateConfirmation Confirm(params CarryoverDecision[] carryover) =>
        new([.. carryover], [], [], []);

    // Seeds a live gen-2 wall of TWO panels — centre (0,0) and right (1,0) — each with a live hold on a
    // real WallPanelId, which is what per-panel scoping keys on.
    private static async Task<TwoPanelWall> SeedTwoPanelWallAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        db.Users.Add(h.Owner);

        var wall = new Wall
        {
            Name = "Attic",
            OwnerId = h.Owner.Id,
            CurrentGeneration = 2,
            Photo = [1, 2, 3],
            PhotoContentType = "image/jpeg",
            UsesMultipleImages = true,
        };
        db.Walls.Add(wall);
        db.WallMembers.Add(new WallMember { WallId = wall.Id, UserId = h.Owner.Id, Role = WallRole.Admin });

        var centre = new WallPanel { WallId = wall.Id, Col = 0, Row = 0, Photo = [1, 2, 3], PhotoContentType = "image/jpeg", Generation = 2 };
        var right = new WallPanel { WallId = wall.Id, Col = 1, Row = 0, Photo = [4, 5, 6], PhotoContentType = "image/jpeg", Generation = 2 };
        db.WallPanels.AddRange(centre, right);

        var centreHold = new Hold { WallId = wall.Id, WallPanelId = centre.Id, X = 0.30, Y = 0.30, Radius = 0.02, Generation = 2 };
        var rightHold = new Hold { WallId = wall.Id, WallPanelId = right.Id, X = 0.70, Y = 0.40, Radius = 0.02, Generation = 2 };
        db.Holds.AddRange(centreHold, rightHold);

        await db.SaveChangesAsync();
        return new TwoPanelWall(wall.Id, centre.Id, right.Id, centreHold.Id, rightHold.Id);
    }

    // Stages a centre-only update: a fresh centre panel at gen 3 with one staged detection, exactly as
    // StartAsync would leave the DB before promote.
    private static async Task<(Guid PanelId, Guid StagedHoldId)> StageCentreUpdateAsync(WallTestHarness h, Guid wallId)
    {
        await using var db = h.CreateContext();
        var panel = new WallPanel
        {
            WallId = wallId,
            Col = 0,
            Row = 0,
            Photo = null,
            StagedPhoto = [7, 8, 9],
            StagedPhotoContentType = "image/jpeg",
            Generation = 3,
        };
        db.WallPanels.Add(panel);

        var staged = new Hold
        {
            WallId = wallId,
            WallPanelId = panel.Id,
            X = 0.31,
            Y = 0.31,
            Radius = 0.02,
            Generation = 3,
            IsAutoDetected = true,
            NeedsReview = true,
        };
        db.Holds.Add(staged);
        await db.SaveChangesAsync();
        return (panel.Id, staged.Id);
    }

    private sealed record TwoPanelWall(
        Guid WallId,
        Guid CentrePanelId,
        Guid RightPanelId,
        Guid CentreHoldId,
        Guid RightHoldId);
}
