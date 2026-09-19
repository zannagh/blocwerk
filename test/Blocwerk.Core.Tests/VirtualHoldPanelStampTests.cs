using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Cover for the panel stamp a virtual hold carries. Historically <c>AddHoldAsync</c> never set
/// <c>WallPanelId</c>, so every virtual hold was panel-less and only rendered because the viewer and
/// the editors fall back to the live CENTER panel (Col 0, Row 0). Create, merge and promote must now
/// write that panel down explicitly — without ever moving a hold that renders today, and without
/// rewriting <c>Generation</c>, which keys cross-generation lineage and the change journal.
/// </summary>
public class VirtualHoldPanelStampTests
{
    private const int WallGeneration = 5;

    [Fact]
    public async Task AddHold_WithPanel_StampsThePanelAndItsOwnGeneration()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0, generation: WallGeneration);

        // A center panel left behind by a subset promote: live, but a generation below the wall.
        var centerId = await SeedCenterPanelAsync(h, generation: WallGeneration - 1);

        var hold = await h.WallService.AddHoldAsync(
            h.WallId, 0.4, 0.4, 0.02, "red", isVirtual: true, wallPanelId: centerId);

        var saved = await LoadHoldAsync(h, hold.Id);

        Assert.Equal(centerId, saved.WallPanelId);

        // The PANEL's generation, never the wall's — the panel overlay reads key on the panel row.
        Assert.Equal(WallGeneration - 1, saved.Generation);
    }

    [Fact]
    public async Task AddHold_WhileWallStaged_TakesNoStamp()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0, generation: WallGeneration);
        var centerId = await SeedCenterPanelAsync(h, generation: WallGeneration);
        await StageWallAsync(h);

        var hold = await h.WallService.AddHoldAsync(
            h.WallId, 0.4, 0.4, 0.02, "red", isVirtual: true, wallPanelId: centerId);

        var saved = await LoadHoldAsync(h, hold.Id);

        // The staged set lives at CurrentGeneration + 1; a live panel stamp would vanish at promotion.
        Assert.Null(saved.WallPanelId);
        Assert.Equal(WallGeneration + 1, saved.Generation);
    }

    [Fact]
    public async Task Merge_MovesSurvivorOntoTheTargetPanelAndGeneration()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0, generation: WallGeneration);
        var centerId = await SeedCenterPanelAsync(h, generation: WallGeneration);
        var rightId = await SeedPanelAsync(h, col: 1, row: 0, generation: WallGeneration - 1);

        var virtualHold = await SeedHoldAsync(h, panelId: null, generation: WallGeneration, isVirtual: true);
        var detected = await SeedHoldAsync(h, panelId: rightId, generation: WallGeneration - 1, isVirtual: false);

        await h.WallService.MergeVirtualHoldAsync(virtualHold, detected);

        var survivor = await LoadHoldAsync(h, virtualHold);

        // The survivor adopted the detected hold's panel-LOCAL coordinates, so it must adopt its panel
        // and that panel's generation too, or it would be drawn against the wrong image.
        Assert.Equal(rightId, survivor.WallPanelId);
        Assert.Equal(WallGeneration - 1, survivor.Generation);
        Assert.False(survivor.IsVirtual);
        Assert.NotEqual(centerId, survivor.WallPanelId);
    }

    [Fact]
    public async Task Merge_WithPanellessTarget_FallsBackToTheCenterPanel()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0, generation: WallGeneration);
        var centerId = await SeedCenterPanelAsync(h, generation: WallGeneration - 1);

        var virtualHold = await SeedHoldAsync(h, panelId: null, generation: WallGeneration, isVirtual: true);
        var legacyTarget = await SeedHoldAsync(h, panelId: null, generation: WallGeneration, isVirtual: false);

        await h.WallService.MergeVirtualHoldAsync(virtualHold, legacyTarget);

        var survivor = await LoadHoldAsync(h, virtualHold);

        // Nothing to inherit, so the survivor is pinned to the panel it already rendered on, and its
        // generation is left exactly where it was.
        Assert.Equal(centerId, survivor.WallPanelId);
        Assert.Equal(WallGeneration, survivor.Generation);
    }

    [Fact]
    public async Task Promote_StampsTheCenterPanelAndLeavesGenerationAlone()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0, generation: WallGeneration);

        // A center panel a generation behind the wall, as a subset promote leaves it.
        var centerId = await SeedCenterPanelAsync(h, generation: WallGeneration - 1);
        var virtualHold = await SeedHoldAsync(h, panelId: null, generation: WallGeneration, isVirtual: true);

        await h.WallService.PromoteVirtualHoldAsync(virtualHold);

        var promoted = await LoadHoldAsync(h, virtualHold);

        Assert.Equal(centerId, promoted.WallPanelId);
        Assert.False(promoted.IsVirtual);

        // Generation keys HoldGenerationLink, the outdated flag and the change journal; a promote is
        // not a generation change.
        Assert.Equal(WallGeneration, promoted.Generation);
    }

    [Fact]
    public async Task Promote_KeepsAnExistingPanel()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0, generation: WallGeneration);
        var centerId = await SeedCenterPanelAsync(h, generation: WallGeneration);
        var rightId = await SeedPanelAsync(h, col: 1, row: 0, generation: WallGeneration);

        var virtualHold = await SeedHoldAsync(h, panelId: rightId, generation: WallGeneration, isVirtual: true);

        await h.WallService.PromoteVirtualHoldAsync(virtualHold);

        var promoted = await LoadHoldAsync(h, virtualHold);

        Assert.Equal(rightId, promoted.WallPanelId);
        Assert.NotEqual(centerId, promoted.WallPanelId);
    }

    [Fact]
    public async Task Promote_WithoutCenterPanel_LeavesTheHoldPanelless()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0, generation: WallGeneration);
        var virtualHold = await SeedHoldAsync(h, panelId: null, generation: WallGeneration, isVirtual: true);

        await h.WallService.PromoteVirtualHoldAsync(virtualHold);

        var promoted = await LoadHoldAsync(h, virtualHold);

        // A legacy single-image wall has no panel to point at; the null-panel fallback still renders it.
        Assert.Null(promoted.WallPanelId);
        Assert.False(promoted.IsVirtual);
    }

    [Fact]
    public async Task Promote_WhileWallStaged_TakesNoStamp()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0, generation: WallGeneration);
        await SeedCenterPanelAsync(h, generation: WallGeneration);
        var virtualHold = await SeedHoldAsync(h, panelId: null, generation: WallGeneration + 1, isVirtual: true);
        await StageWallAsync(h);

        await h.WallService.PromoteVirtualHoldAsync(virtualHold);

        var promoted = await LoadHoldAsync(h, virtualHold);

        Assert.Null(promoted.WallPanelId);
        Assert.Equal(WallGeneration + 1, promoted.Generation);
    }

    private static Task<Guid> SeedCenterPanelAsync(WallTestHarness h, int generation) =>
        SeedPanelAsync(h, col: 0, row: 0, generation: generation);

    private static async Task<Guid> SeedPanelAsync(WallTestHarness h, int col, int row, int generation)
    {
        await using var db = h.CreateContext();

        var panel = new WallPanel
        {
            WallId = h.WallId,
            Col = col,
            Row = row,
            Photo = [1, 2, 3],
            PhotoContentType = "image/jpeg",
            Generation = generation,
        };
        db.WallPanels.Add(panel);
        await db.SaveChangesAsync();
        return panel.Id;
    }

    private static async Task<Guid> SeedHoldAsync(WallTestHarness h, Guid? panelId, int generation, bool isVirtual)
    {
        await using var db = h.CreateContext();

        var hold = new Hold
        {
            WallId = h.WallId,
            WallPanelId = panelId,
            X = 0.3,
            Y = 0.3,
            Radius = 0.02,
            Generation = generation,
            IsVirtual = isVirtual,
        };
        db.Holds.Add(hold);
        await db.SaveChangesAsync();
        return hold.Id;
    }

    private static async Task StageWallAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();

        var wall = await db.Walls.FirstAsync(w => w.Id == h.WallId);
        wall.StagedPhoto = [4, 5, 6];
        wall.StagedPhotoContentType = "image/jpeg";
        wall.StagedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    private static async Task<Hold> LoadHoldAsync(WallTestHarness h, Guid holdId)
    {
        await using var db = h.CreateContext();
        return await db.Holds.AsNoTracking().FirstAsync(x => x.Id == holdId);
    }
}
