using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Stale glyph measurements (the rules on <c>Hold.Glyph.cs</c>): a move drops the plane position (these
/// walls have no model to re-place it from), a reshape/resize only the size — never the position — a
/// "changed" mark the sizes, footprint and fingerprint, and a save that changes no geometry drops nothing.
/// </summary>
public class GlyphInvalidationTests
{
    [Fact]
    public async Task UpdateHold_Move_ClearsPositionKeepsSize()
    {
        using var h = new WallTestHarness();
        var hold = (await h.SeedWallAsync(holdCount: 1))[0];
        await MeasureAsync(h, hold.Id);

        await h.WallService.UpdateHoldAsync(hold.Id, Edit(0.3, 0.3, 0.02));

        var stored = await LoadAsync(h, hold.Id);
        Assert.Null(stored.FacetId);
        Assert.Null(stored.PlaneAMm);
        Assert.Null(stored.PlaneBMm);
        Assert.Null(stored.MetricSource);
        Assert.Equal(40, stored.WidthMm);
        Assert.Equal(120, HoldFingerprint.FromJson(stored.FingerprintJson)!.L);
        Assert.Equal(40, HoldFingerprint.FromJson(stored.FingerprintJson)!.WidthMm);
    }

    [Fact]
    public async Task UpdateHold_Resize_ClearsSizeButKeepsPosition_AndStripsFingerprintMillimetres()
    {
        using var h = new WallTestHarness();
        var hold = (await h.SeedWallAsync(holdCount: 1))[0];
        await MeasureAsync(h, hold.Id);

        // The editor's resize slider redraws a shaped hold's outline at the new size.
        await h.WallService.UpdateHoldAsync(hold.Id, Edit(0.1, 0.1, 0.05, ShapePoint.DefaultOctagon(0.05)));

        var stored = await LoadAsync(h, hold.Id);
        Assert.Equal("0", stored.FacetId);
        Assert.Equal(100, stored.PlaneAMm);
        Assert.Equal(200, stored.PlaneBMm);
        Assert.Null(stored.WidthMm);
        Assert.Null(stored.HeightMm);
        Assert.Null(stored.AreaMm2);
        Assert.Equal(HoldOutlineSource.Manual, stored.OutlineSource);
        var fingerprint = HoldFingerprint.FromJson(stored.FingerprintJson)!;
        Assert.Equal(120, fingerprint.L);
        Assert.Null(fingerprint.WidthMm);
    }

    [Fact]
    public async Task UpdateHold_SameGeometry_KeepsEverything()
    {
        using var h = new WallTestHarness();
        var hold = (await h.SeedWallAsync(holdCount: 1))[0];
        await MeasureAsync(h, hold.Id);

        await h.WallService.UpdateHoldAsync(hold.Id, Edit(0.1, 0.1, 0.02, ShapePoint.DefaultOctagon(0.02)));

        var stored = await LoadAsync(h, hold.Id);
        Assert.Equal("0", stored.FacetId);
        Assert.Equal(40, stored.WidthMm);
        Assert.Equal(HoldOutlineSource.AutoContour, stored.OutlineSource);
    }

    [Fact]
    public async Task MarkHoldModified_ClearsSizesAndTheFingerprint_ButKeepsThePosition()
    {
        using var h = new WallTestHarness();
        var hold = (await h.SeedWallAsync(holdCount: 1))[0];
        await MeasureAsync(h, hold.Id);

        await h.WallService.MarkHoldModifiedAsync(hold.Id);

        var stored = await LoadAsync(h, hold.Id);
        Assert.True(stored.NeedsReview);
        Assert.Equal("0", stored.FacetId);
        Assert.Equal(100, stored.PlaneAMm);
        Assert.Null(stored.WidthMm);
        Assert.Null(stored.AreaMm2);
        Assert.Null(stored.FingerprintJson);
    }

    [Fact]
    public async Task UpdateStagedHold_MoveClearsPosition_ResizeOfACircleClearsSize()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var holdId = await SeedStagedHoldAsync(h);
        await MeasureAsync(h, holdId, outline: false);
        var service = StagedService(h);

        await service.UpdateStagedHoldAsync(h.WallId, holdId, 0.6, 0.6, 0.02);
        var moved = await LoadAsync(h, holdId);
        Assert.Null(moved.PlaneAMm);
        Assert.Equal(40, moved.WidthMm);

        await service.UpdateStagedHoldAsync(h.WallId, holdId, 0.6, 0.6, 0.04);
        Assert.Null((await LoadAsync(h, holdId)).WidthMm);
    }

    [Fact]
    public async Task UpdateStagedHold_RadiusOnlyOnAShapedHold_KeepsSizeAndOutlineSource()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var holdId = await SeedStagedHoldAsync(h);
        await MeasureAsync(h, holdId);

        // Drawn from its outline: the radius is only the hit size, so the measured shape still stands.
        await StagedService(h).UpdateStagedHoldAsync(h.WallId, holdId, 0.5, 0.5, 0.04);

        var stored = await LoadAsync(h, holdId);
        Assert.Equal(0.04, stored.Radius, 6);
        Assert.Equal(40, stored.WidthMm);
        Assert.Equal(HoldOutlineSource.AutoContour, stored.OutlineSource);
    }

    [Fact]
    public void Clone_KeepsMetrics_ForTheSamePhysicalHold()
    {
        var hold = new Hold { FacetId = "0", PlaneAMm = 1, WidthMm = 2, FingerprintJson = "{}" };

        var clone = hold.Clone();

        Assert.Equal("0", clone.FacetId);
        Assert.Equal(1, clone.PlaneAMm);
        Assert.Equal(2, clone.WidthMm);
        Assert.Equal("{}", clone.FingerprintJson);
    }

    private static HoldEdit Edit(double x, double y, double radius, List<ShapePoint>? shape = null) =>
        HoldEdit.FromEditorState(x, y, radius, null, HoldCategory.Hand, false, shape, null, null, null, flagBouldersOnMove: false);

    private static WallPanelService StagedService(WallTestHarness h) => new(
        h.DbContextFactory, h.CurrentUser, h.HoldDetection, Substitute.For<IHoldOverlapMatcher>(),
        NullLogger<WallPanelService>.Instance);

    private static async Task MeasureAsync(WallTestHarness h, Guid holdId, bool outline = true)
    {
        await using var db = h.CreateContext();
        var hold = await db.Holds.SingleAsync(x => x.Id == holdId);
        hold.ShapePoints = outline ? ShapePoint.DefaultOctagon(0.02) : null;
        hold.OutlineSource = HoldOutlineSource.AutoContour;
        hold.WidthMm = 40;
        hold.HeightMm = 30;
        hold.AreaMm2 = 900;
        hold.FacetId = "0";
        hold.PlaneAMm = 100;
        hold.PlaneBMm = 200;
        hold.MetricSource = "multi-marker";
        hold.FingerprintJson = new HoldFingerprint { L = 120, WidthMm = 40, HeightMm = 30, AreaMm2 = 900 }.ToJson();
        await db.SaveChangesAsync();
    }

    private static async Task<Hold> LoadAsync(WallTestHarness h, Guid holdId)
    {
        await using var db = h.CreateContext();
        return await db.Holds.AsNoTracking().SingleAsync(x => x.Id == holdId);
    }

    private static async Task<Guid> SeedStagedHoldAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        var panel = new WallPanel
        {
            WallId = h.WallId, Col = 0, Row = 0, StagedPhoto = [1, 2, 3], StagedPhotoContentType = "image/jpeg", Generation = 1,
        };
        var hold = new Hold { WallId = h.WallId, WallPanelId = panel.Id, X = 0.5, Y = 0.5, Radius = 0.02, Generation = 1 };
        db.WallPanels.Add(panel);
        db.Holds.Add(hold);
        await db.SaveChangesAsync();
        return hold.Id;
    }
}
