using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// What a live edit on ONE panel does to the same physical hold's copies on the overlapping panels it is
/// linked to: the hold's name follows it (a name is a physical label, unlike geometry, which is a
/// per-photo fact), a blank name never blanks a named twin, and a CHANGED verdict fans out to peripheral
/// twins exactly as the UNCHANGED verdict already did.
/// </summary>
public class HoldTwinPropagationTests
{
    [Fact]
    public async Task UpdateHold_Rename_PropagatesToLinkedTwin()
    {
        using var h = new WallTestHarness();
        var (centre, twin) = await SeedLinkedPairAsync(h);

        await h.WallService.UpdateHoldAsync(centre.Id, new HoldEdit { X = centre.X, Y = centre.Y, Radius = centre.Radius, Name = "Blue Pinch" });

        await using var db = h.CreateContext();
        Assert.Equal("Blue Pinch", (await db.Holds.SingleAsync(x => x.Id == twin.Id)).Name);
    }

    // Renaming the PERIPHERAL copy must reach the centre too: the edited hold is the source, not the
    // more central one.
    [Fact]
    public async Task UpdateHold_RenameOnPeripheralPanel_PropagatesInward()
    {
        using var h = new WallTestHarness();
        var (centre, twin) = await SeedLinkedPairAsync(h);

        await h.WallService.UpdateHoldAsync(twin.Id, new HoldEdit { X = twin.X, Y = twin.Y, Radius = twin.Radius, Name = "Far Jug" });

        await using var db = h.CreateContext();
        Assert.Equal("Far Jug", (await db.Holds.SingleAsync(x => x.Id == centre.Id)).Name);
    }

    // An edit that carries no name (the normal case — most holds are unnamed) must NOT blank a twin that
    // the user deliberately named.
    [Fact]
    public async Task UpdateHold_EmptyName_DoesNotBlankNamedTwin()
    {
        using var h = new WallTestHarness();
        var (centre, twin) = await SeedLinkedPairAsync(h);
        await SetNameAsync(h, twin.Id, "Keeps Its Name");

        await h.WallService.UpdateHoldAsync(centre.Id, new HoldEdit { X = centre.X, Y = centre.Y, Radius = centre.Radius, Color = "red", Name = "" });

        await using var db = h.CreateContext();
        Assert.Equal("Keeps Its Name", (await db.Holds.SingleAsync(x => x.Id == twin.Id)).Name);
        // The rest of the appearance still propagates verbatim.
        Assert.Equal("red", (await db.Holds.SingleAsync(x => x.Id == twin.Id)).Color);
    }

    // D4. Marking a hold CHANGED flags its peripheral twin, mirroring the unchanged verdict's fan-out —
    // otherwise a hold is flagged on one panel and clean on its twin.
    [Fact]
    public async Task MarkHoldModified_FlagsPeripheralTwinAndItsBoulders()
    {
        using var h = new WallTestHarness();
        var (centre, twin) = await SeedLinkedPairAsync(h);
        var twinBoulderId = await AttachBoulderAsync(h, twin.Id);

        await h.WallService.MarkHoldModifiedAsync(centre.Id);

        await using var db = h.CreateContext();
        Assert.True((await db.Holds.SingleAsync(x => x.Id == centre.Id)).NeedsReview);
        Assert.True((await db.Holds.SingleAsync(x => x.Id == twin.Id)).NeedsReview);
        Assert.True((await db.Boulders.SingleAsync(b => b.Id == twinBoulderId)).NeedsReview);
    }

    // The fan-out runs centre-outward only: marking the PERIPHERAL copy changed leaves the more central
    // one alone, the same asymmetry GetPeripheralTwinIdsAsync already gives the unchanged verdict.
    [Fact]
    public async Task MarkHoldModified_FromPeripheralPanel_DoesNotFlagTheCentre()
    {
        using var h = new WallTestHarness();
        var (centre, twin) = await SeedLinkedPairAsync(h);

        await h.WallService.MarkHoldModifiedAsync(twin.Id);

        await using var db = h.CreateContext();
        Assert.True((await db.Holds.SingleAsync(x => x.Id == twin.Id)).NeedsReview);
        Assert.False((await db.Holds.SingleAsync(x => x.Id == centre.Id)).NeedsReview);
    }

    // The startup backfill FILLS gaps and never overwrites, so a deliberate edit on a peripheral panel
    // survives the next restart instead of being reverted to the centre's value.
    [Fact]
    public void FillMissingAppearance_NeverOverwritesASetValue_ButFillsAnUnsetOne()
    {
        var source = new Hold { Name = "Centre", Color = "blue", Material = HoldMaterial.PU, Category = HoldCategory.Foot };
        var edited = new Hold { Name = "Edited on the edge", Color = "red", Category = HoldCategory.Hand };
        var blank = new Hold();

        HoldPropertySync.FillMissingAppearance(source, edited);
        // Values the user set on the peripheral copy stand; only its genuine gaps are filled (Material
        // was null).
        Assert.Equal("red", edited.Color);
        Assert.Equal("Edited on the edge", edited.Name);
        Assert.Equal(HoldMaterial.PU, edited.Material);

        // Category is NOT a gap that can be filled: it is non-nullable with Hand = 0, so this Hand may
        // equally be a deliberate choice, and copying the source's Foot over it reverted the user's edit
        // on every restart. It stays exactly as it was.
        Assert.Equal(HoldCategory.Hand, edited.Category);

        Assert.True(HoldPropertySync.FillMissingAppearance(source, blank));
        Assert.Equal("blue", blank.Color);
        Assert.Equal("Centre", blank.Name);
        Assert.Equal(HoldMaterial.PU, blank.Material);
        Assert.Equal(HoldCategory.Hand, blank.Category);
    }

    // A centre hold on the (0,0) panel linked to a twin on the (1,0) panel.
    private static async Task<(Hold Centre, Hold Twin)> SeedLinkedPairAsync(WallTestHarness h)
    {
        await h.SeedWallAsync(holdCount: 0);
        await using var db = h.CreateContext();

        var centrePanel = NewPanel(h.WallId, col: 0, row: 0);
        var twinPanel = NewPanel(h.WallId, col: 1, row: 0);
        db.WallPanels.AddRange(centrePanel, twinPanel);

        var centre = NewHold(h.WallId, centrePanel.Id, 0.5, 0.5);
        var twin = NewHold(h.WallId, twinPanel.Id, 0.2, 0.5);
        db.Holds.AddRange(centre, twin);
        db.HoldLinks.Add(new HoldLink { WallId = h.WallId, HoldAId = centre.Id, HoldBId = twin.Id });

        await db.SaveChangesAsync();
        return (centre, twin);
    }

    private static WallPanel NewPanel(Guid wallId, int col, int row) => new()
    {
        WallId = wallId,
        Col = col,
        Row = row,
        Photo = [1, 2, 3],
        PhotoContentType = "image/jpeg",
        Generation = 0,
    };

    private static Hold NewHold(Guid wallId, Guid panelId, double x, double y) => new()
    {
        WallId = wallId,
        WallPanelId = panelId,
        X = x,
        Y = y,
        Radius = 0.02,
        Generation = 0,
    };

    private static async Task SetNameAsync(WallTestHarness h, Guid holdId, string name)
    {
        await using var db = h.CreateContext();
        var hold = await db.Holds.SingleAsync(x => x.Id == holdId);
        hold.Name = name;
        await db.SaveChangesAsync();
    }

    private static async Task<Guid> AttachBoulderAsync(WallTestHarness h, Guid holdId)
    {
        await using var db = h.CreateContext();
        var boulder = new Boulder
        {
            WallId = h.WallId,
            Name = "Uses the twin",
            CreatedByUserId = h.Owner.Id,
            Generation = 0,
        };
        db.Boulders.Add(boulder);
        db.BoulderHolds.Add(new BoulderHold { BoulderId = boulder.Id, HoldId = holdId });
        await db.SaveChangesAsync();
        return boulder.Id;
    }
}
