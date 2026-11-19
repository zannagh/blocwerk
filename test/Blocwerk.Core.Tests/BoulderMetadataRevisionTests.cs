using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Web.State;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The full editor is also the rename/regrade path: clearing a grade works, names are trimmed, and a save that did not
/// edit any hold changes the metadata only (stored holds and the review flag stay), while a historic boulder keeps
/// needing its holds. Plus the two page rules that go with it.
/// </summary>
public class BoulderMetadataRevisionTests
{
    [Fact]
    public async Task Revise_WithAnEmptyGrade_ClearsIt_AndNullLeavesItAlone()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        List<BoulderHoldInput> same = [new BoulderHoldInput(holds[0].Id)];
        var boulder = await h.BoulderService.CreateBoulderAsync(h.WallId, "A", "6A", same);

        await h.BoulderService.ReviseBoulderAsync(boulder.Id, same, name: "A", grade: null);
        Assert.Equal("6A", (await StoredAsync(h, boulder.Id)).Grade);

        await h.BoulderService.ReviseBoulderAsync(boulder.Id, same, name: "A", grade: string.Empty);
        Assert.Null((await StoredAsync(h, boulder.Id)).Grade);

        // Clearing again is a replay of the same state, not an error.
        await h.BoulderService.ReviseBoulderAsync(boulder.Id, same, name: "A", grade: string.Empty);
        Assert.Null((await StoredAsync(h, boulder.Id)).Grade);
    }

    [Fact]
    public async Task Revise_TrimsTheName()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        List<BoulderHoldInput> same = [new BoulderHoldInput(holds[0].Id)];
        var boulder = await h.BoulderService.CreateBoulderAsync(h.WallId, "A", null, same);

        await h.BoulderService.ReviseBoulderAsync(boulder.Id, same, name: "  Better name  ");

        Assert.Equal("Better name", (await StoredAsync(h, boulder.Id)).Name);
    }

    [Fact]
    public async Task AMetadataOnlySave_KeepsTheHoldsAndTheReviewFlag()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 2);
        var boulder = await h.BoulderService.CreateBoulderAsync(
            h.WallId, "Typo", "6A", [new BoulderHoldInput(holds[0].Id, HoldType.Start), new BoulderHoldInput(holds[1].Id)]);
        await using (var db = h.CreateContext())
        {
            (await db.Boulders.FirstAsync(b => b.Id == boulder.Id)).NeedsReview = true;
            await db.SaveChangesAsync();
        }

        await h.BoulderService.ReviseBoulderAsync(boulder.Id, null, name: "Fixed", grade: "6B");

        var saved = await StoredAsync(h, boulder.Id);
        Assert.Equal("Fixed", saved.Name);
        Assert.Equal("6B", saved.Grade);
        Assert.True(saved.NeedsReview);
        Assert.Equal(2, saved.BoulderHolds.Count);
        Assert.Contains(saved.BoulderHolds, x => x.HoldId == holds[0].Id && x.Type == HoldType.Start);
    }

    [Fact]
    public async Task ASaveThatSuppliesHolds_StillSignsOffTheReview()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        List<BoulderHoldInput> same = [new BoulderHoldInput(holds[0].Id)];
        var boulder = await h.BoulderService.CreateBoulderAsync(h.WallId, "A", null, same);
        await using (var db = h.CreateContext())
        {
            (await db.Boulders.FirstAsync(b => b.Id == boulder.Id)).NeedsReview = true;
            await db.SaveChangesAsync();
        }

        await h.BoulderService.ReviseBoulderAsync(boulder.Id, same, name: "A");

        Assert.False((await StoredAsync(h, boulder.Id)).NeedsReview);
    }

    [Fact]
    public async Task AHistoricBoulder_CannotBeSavedWithoutHolds()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 1);
        var boulder = await h.BoulderService.CreateBoulderAsync(h.WallId, "A", null, [new BoulderHoldInput(holds[0].Id)]);
        await using (var db = h.CreateContext())
        {
            (await db.Boulders.FirstAsync(b => b.Id == boulder.Id)).IsHistoric = true;
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.BoulderService.ReviseBoulderAsync(boulder.Id, null, name: "B"));
        Assert.True((await StoredAsync(h, boulder.Id)).IsHistoric);
    }

    [Theory]
    [InlineData(true, true, false, false, true)]
    [InlineData(true, false, true, false, true)]
    [InlineData(true, false, false, true, true)]
    [InlineData(false, false, false, true, false)]
    [InlineData(true, false, false, false, false)]
    public void ThePillsAreOfferedForABoulderThatNeedsALook(bool hasThen, bool needsReview, bool isHistoric, bool showingThen, bool expected) =>
        Assert.Equal(expected, BoulderReviseRules.OfferGenerationToggle(hasThen, needsReview, isHistoric, showingThen));

    [Theory]
    [InlineData(false, false, false, false, false)]
    [InlineData(true, false, false, false, true)]
    [InlineData(false, true, false, false, true)]
    [InlineData(false, false, true, false, true)]
    [InlineData(false, false, false, true, true)]
    public void AnEditorSaveReplacesHoldsOnlyWhenTheyWereEditedOrTheReviewConfirmed(bool draft, bool historic, bool review, bool edited, bool expected) =>
        Assert.Equal(expected, BoulderReviseRules.ReplacesHolds(draft, historic, review, edited));

    private static async Task<Boulder> StoredAsync(WallTestHarness h, Guid id)
    {
        await using var db = h.CreateContext();
        return await db.Boulders.Include(b => b.BoulderHolds).FirstAsync(b => b.Id == id);
    }
}
