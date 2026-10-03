using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Detection.Enrichment;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>Reverting an outline upgrade, who may run it, and the marker-wall metric fill.</summary>
public class HoldOutlineUpgradeRevertTests
{
    [Fact]
    public async Task Revert_RestoresExactly_AndLeavesEditedHoldsAlone()
    {
        using var h = new WallTestHarness();
        var s = await OutlineUpgradeScenario.CreateAsync(h);
        var before = await s.LoadHoldsAsync();
        var service = s.Service();
        var run = await service.ApplyAsync(h.WallId, new HoldOutlineUpgradeOptions());

        // The user redraws one of the new outlines in the editor.
        await using (var db = h.CreateContext())
        {
            var edited = await db.Holds.SingleAsync(x => x.Id == s.AutoWithFingerprint);
            edited.ShapePoints = ShapePoint.DefaultOctagon(0.05);
            edited.InvalidateGlyphForEdit(moved: false, reshaped: true);
            await db.SaveChangesAsync();
        }

        var revert = await service.RevertAsync(h.WallId, run.RunId);

        Assert.Equal(2, revert.Reverted);
        Assert.Equal([s.AutoWithFingerprint], revert.SkippedEdited);
        Assert.Equal(0, revert.Missing);
        var after = await s.LoadHoldsAsync();
        HoldOutlineUpgradeServiceTests.AssertUnchanged(before[s.AutoOutlined], after[s.AutoOutlined]);
        HoldOutlineUpgradeServiceTests.AssertUnchanged(before[s.AutoKeepsCircle], after[s.AutoKeepsCircle]);
        Assert.Equal(8, after[s.AutoWithFingerprint].ShapePoints!.Count);
        Assert.Equal(HoldOutlineSource.Manual, after[s.AutoWithFingerprint].OutlineSource);

        await using var read = h.CreateContext();
        Assert.NotNull((await read.HoldOutlineUpgradeRuns.SingleAsync()).RevertedAt);
        Assert.NotNull((await service.GetStatusAsync(h.WallId)).LatestRun!.RevertedAt);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => service.RevertAsync(h.WallId, run.RunId));
    }

    [Fact]
    public async Task Revert_CountsHoldsDeletedSinceTheRun()
    {
        using var h = new WallTestHarness();
        var s = await OutlineUpgradeScenario.CreateAsync(h);
        var run = await s.Service().ApplyAsync(h.WallId, new HoldOutlineUpgradeOptions());
        await using (var db = h.CreateContext())
        {
            db.Holds.Remove(await db.Holds.SingleAsync(x => x.Id == s.AutoOutlined));
            await db.SaveChangesAsync();
        }

        var revert = await s.Service().RevertAsync(h.WallId, run.RunId);

        Assert.Equal((2, 1), (revert.Reverted, revert.Missing));
    }

    [Theory]
    [InlineData(WallRole.Member)]
    [InlineData(WallRole.Moderator)]
    public async Task NonAdmin_IsRefused_AndNothingChanges(WallRole role)
    {
        using var h = new WallTestHarness();
        var s = await OutlineUpgradeScenario.CreateAsync(h);
        var run = await s.Service().ApplyAsync(h.WallId, new HoldOutlineUpgradeOptions());
        var before = await s.LoadHoldsAsync();
        h.ActingUser = await h.AddMemberAsync("climber@test", role);
        var service = s.Service();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetStatusAsync(h.WallId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.PreviewAsync(h.WallId, new HoldOutlineUpgradeOptions()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ApplyAsync(h.WallId, new HoldOutlineUpgradeOptions(true)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RevertAsync(h.WallId, run.RunId));

        var after = await s.LoadHoldsAsync();
        Assert.All(before.Values, b => HoldOutlineUpgradeServiceTests.AssertUnchanged(b, after[b.Id]));
        await using var db = h.CreateContext();
        Assert.Equal(1, await db.HoldOutlineUpgradeRuns.CountAsync());
    }

    [Fact]
    public async Task Kiosk_IsRefused_EvenActingAsTheOwnerOfItsOwnWall()
    {
        using var h = new WallTestHarness();
        var s = await OutlineUpgradeScenario.CreateAsync(h);
        var kiosk = Substitute.For<IKioskContext>();
        kiosk.IsKiosk.Returns(true);
        kiosk.KioskWallId.Returns(h.WallId);
        var service = s.Service(kiosk: kiosk);

        await Assert.ThrowsAsync<KioskRestrictedException>(() => service.PreviewAsync(h.WallId, new HoldOutlineUpgradeOptions()));
        await Assert.ThrowsAsync<KioskRestrictedException>(() => service.ApplyAsync(h.WallId, new HoldOutlineUpgradeOptions()));

        await using var db = h.CreateContext();
        Assert.False(await db.Holds.AnyAsync(x => x.OutlineSource == HoldOutlineSource.AutoContour));
    }

    [Fact]
    public async Task Admin_WhoIsNotTheOwner_CanRunIt()
    {
        using var h = new WallTestHarness();
        var s = await OutlineUpgradeScenario.CreateAsync(h);
        h.ActingUser = await h.AddMemberAsync("coowner@test", WallRole.Admin);

        var result = await s.Service().ApplyAsync(h.WallId, new HoldOutlineUpgradeOptions());

        Assert.Equal(2, result.Outlined);
    }

    [Fact]
    public async Task MarkerWall_WithModelAndObservations_GetsMillimetres_AndRevertRestoresThem()
    {
        using var h = new WallTestHarness();
        var s = await OutlineUpgradeScenario.CreateAsync(h);
        await EnrichmentScenario.AddActiveModelAsync(h, EnrichmentScenario.TwoFacetGeometryJson);
        await using (var db = h.CreateContext())
        {
            (await db.Walls.SingleAsync()).GlyphsEnabled = true;
            db.WallMarkerObservations.AddRange(
                Observation(s.PanelId, EnrichmentFakes.Square(0, 100, 100, 100)),
                Observation(s.PanelId, EnrichmentFakes.Square(1, 800, 100, 100)));
            await db.SaveChangesAsync();
        }

        var result = await s.Service().ApplyAsync(h.WallId, new HoldOutlineUpgradeOptions());

        Assert.Equal(2, result.Measured);
        var measured = (await s.LoadHoldsAsync())[s.AutoOutlined];
        Assert.Equal("0", measured.FacetId);
        Assert.Equal(50, measured.WidthMm!.Value, 1);
        Assert.Equal(75, measured.HeightMm!.Value, 1);

        await s.Service().RevertAsync(h.WallId, result.RunId);
        var reverted = (await s.LoadHoldsAsync())[s.AutoOutlined];
        Assert.Null(reverted.WidthMm);
        Assert.Null(reverted.FacetId);
        Assert.Null(reverted.FingerprintJson);
    }

    private static WallMarkerObservation Observation(Guid panelId, DetectedMarker marker) => new()
    {
        WallPanelId = panelId,
        PanelGeneration = 1,
        FromStagedPhoto = false,
        MarkerId = marker.Id,
        CornersJson = System.Text.Json.JsonSerializer.Serialize(marker.CornersNormalized.Select(c => new[] { c.X, c.Y })),
        SidePx = marker.SidePx,
    };
}
