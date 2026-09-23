// <copyright file="HoldPocketTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Detection.Enrichment;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <see cref="Hold.ShapeHoles"/>: persisted like the outline, cloned, written by enrichment, dropped by a
/// manual reshape (one rule, <see cref="Hold.InvalidateGlyphForEdit"/>), and subtracted from the metric area.
/// </summary>
public class HoldPocketTests
{
    [Fact]
    public async Task ShapeHoles_RoundTripThroughTheDatabase()
    {
        using var h = new WallTestHarness();
        var hold = (await h.SeedWallAsync(holdCount: 1))[0];
        await SetPocketAsync(h, hold.Id);

        var stored = await LoadAsync(h, hold.Id);

        Assert.NotNull(stored.ShapeHoles);
        Assert.Single(stored.ShapeHoles!);
        Assert.Equal(-0.004, stored.ShapeHoles![0][0].Dx);
        Assert.Equal(0.005, stored.ShapeHoles[0][2].Dy);
    }

    [Fact]
    public async Task UpdateHold_ManualReshape_DropsHoles()
    {
        using var h = new WallTestHarness();
        var hold = (await h.SeedWallAsync(holdCount: 1))[0];
        await SetPocketAsync(h, hold.Id);

        await h.WallService.UpdateHoldAsync(hold.Id, Edit(0.1, 0.1, 0.02, ShapePoint.DefaultOctagon(0.03)));

        var stored = await LoadAsync(h, hold.Id);
        Assert.Null(stored.ShapeHoles);
        Assert.Equal(HoldOutlineSource.Manual, stored.OutlineSource);
    }

    [Fact]
    public async Task UpdateHold_MoveOrUnchangedShape_KeepsHoles()
    {
        using var h = new WallTestHarness();
        var hold = (await h.SeedWallAsync(holdCount: 1))[0];
        await SetPocketAsync(h, hold.Id);

        // Holes are centre-relative like the outline, so a pure move carries them along.
        await h.WallService.UpdateHoldAsync(hold.Id, Edit(0.3, 0.35, 0.02, ShapePoint.DefaultOctagon(0.02)));

        Assert.Single((await LoadAsync(h, hold.Id)).ShapeHoles ?? []);
    }

    [Fact]
    public async Task UpdateHold_RadiusOnlyOnAShapedHold_KeepsHoles()
    {
        using var h = new WallTestHarness();
        var hold = (await h.SeedWallAsync(holdCount: 1))[0];
        await SetPocketAsync(h, hold.Id);

        // No outline in the edit (kept): the drawn shape is unchanged, so the pocket stays.
        await h.WallService.UpdateHoldAsync(hold.Id, Edit(0.1, 0.1, 0.04, null));

        var stored = await LoadAsync(h, hold.Id);
        Assert.Single(stored.ShapeHoles ?? []);
        Assert.Equal(HoldOutlineSource.AutoContour, stored.OutlineSource);
    }

    [Fact]
    public async Task UpdateStagedHold_RadiusOnlyOnAShapedHold_KeepsHoles()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var holdId = await SeedStagedPocketAsync(h);
        var service = new WallPanelService(
            h.DbContextFactory, h.CurrentUser, h.HoldDetection, Substitute.For<IHoldOverlapMatcher>(),
            NullLogger<WallPanelService>.Instance);

        await service.UpdateStagedHoldAsync(h.WallId, holdId, 0.5, 0.5, 0.05);

        Assert.Single((await LoadAsync(h, holdId)).ShapeHoles ?? []);
    }

    [Fact]
    public void IsReshape_OnlyARealOutlineChangeCountsForAShapedHold()
    {
        var shaped = new Hold { Radius = 0.02, ShapePoints = ShapePoint.DefaultOctagon(0.02) };
        var circle = new Hold { Radius = 0.02 };

        Assert.False(shaped.IsReshape(0.05, shaped.ShapePoints));
        Assert.False(shaped.IsReshape(0.02, ShapePoint.DefaultOctagon(0.02)));
        Assert.True(shaped.IsReshape(0.02, ShapePoint.DefaultOctagon(0.03)));
        Assert.True(shaped.IsReshape(0.02, null));
        Assert.True(circle.IsReshape(0.05, null));
        Assert.False(circle.IsReshape(0.02, null));
        Assert.True(circle.IsReshape(0.02, ShapePoint.DefaultOctagon(0.02)));
    }

    [Fact]
    public void InvalidateGlyphForEdit_ReshapeDropsHoles_MoveKeepsThem()
    {
        var moved = new Hold { ShapePoints = ShapePoint.DefaultOctagon(0.02), ShapeHoles = Ring() };
        moved.InvalidateGlyphForEdit(moved: true, reshaped: false);
        Assert.NotNull(moved.ShapeHoles);

        var reshaped = new Hold { ShapePoints = ShapePoint.DefaultOctagon(0.02), ShapeHoles = Ring() };
        reshaped.InvalidateGlyphForEdit(moved: false, reshaped: true);
        Assert.Null(reshaped.ShapeHoles);
    }

    [Fact]
    public void Clone_DeepCopiesHoles()
    {
        var hold = new Hold { ShapeHoles = Ring() };

        var clone = hold.Clone();

        Assert.NotSame(hold.ShapeHoles, clone.ShapeHoles);
        Assert.NotSame(hold.ShapeHoles![0], clone.ShapeHoles![0]);
        Assert.NotSame(hold.ShapeHoles[0][0], clone.ShapeHoles[0][0]);
        Assert.Equal(hold.ShapeHoles[0][1].Dx, clone.ShapeHoles[0][1].Dx);
    }

    [Fact]
    public void EnrichmentPlan_WritesHolesWithTheOutline()
    {
        using var h = new WallTestHarness();
        using var db = h.CreateContext();
        var hold = new Hold { X = 0.5, Y = 0.5, IsAutoDetected = true };
        var outline = new HoldOutlineResult(
            [new(0.49, 0.49), new(0.51, 0.49), new(0.5, 0.52)], 0.5, 0.5,
            [new() { Dx = -0.01, Dy = -0.01 }, new() { Dx = 0.01, Dy = -0.01 }, new() { Dx = 0, Dy = 0.02 }],
            100, new HoldOutlineBounds(0.49, 0.49, 0.02, 0.03), 0.9, HoldOutlineMethod.Contour, new HoldFingerprint(), Ring());
        var plan = new HoldEnrichmentPlan();
        plan.Outlines[hold] = outline;

        plan.Apply(db);

        Assert.Same(outline.ShapeHoles, hold.ShapeHoles);
        Assert.Equal(HoldOutlineSource.AutoContour, hold.OutlineSource);
    }

    [Fact]
    public void MetricArea_SubtractsHoles()
    {
        // A head-on 100 px marker of 125 mm: 1.25 mm per px. Outer 200 px square, hole 100 px square.
        var maps = MarkerPlaneMapper.MapLocal([EnrichmentFakes.Square(0, 0, 0, 100)], 125);
        MarkerPoint[] outer = [new(300, 300), new(500, 300), new(500, 500), new(300, 500)];
        MarkerPoint[] hole = [new(350, 350), new(450, 350), new(450, 450), new(350, 450)];

        var solid = HoldMetricMeasurer.MeasureLocal(outer, new(400, 400), maps, [EnrichmentFakes.Square(0, 0, 0, 100)]);
        var pocket = HoldMetricMeasurer.MeasureLocal(outer, new(400, 400), maps, [EnrichmentFakes.Square(0, 0, 0, 100)], [hole]);

        Assert.Equal(62500, solid!.AreaMm2, 0);
        Assert.Equal(62500 - 15625, pocket!.AreaMm2, 0);
        Assert.Equal(solid.WidthMm, pocket.WidthMm, 6);
    }

    [Fact]
    public void HolesPx_UsesStoredHolesOnlyWithAStoredOutline()
    {
        var shaped = new Hold { X = 0.5, Y = 0.5, ShapePoints = ShapePoint.DefaultOctagon(0.02), ShapeHoles = Ring() };
        var circle = new Hold { X = 0.5, Y = 0.5, ShapeHoles = Ring() };

        var rings = HoldMetricMeasurer.HolesPx(shaped, null, 1000, 1000);

        Assert.Single(rings);
        Assert.Equal(496, rings[0][0].X, 6);
        Assert.Empty(HoldMetricMeasurer.HolesPx(circle, null, 1000, 1000));
    }

    private static List<List<ShapePoint>> Ring() =>
    [
        [new() { Dx = -0.004, Dy = -0.004 }, new() { Dx = 0.004, Dy = -0.004 }, new() { Dx = 0, Dy = 0.005 }],
    ];

    private static HoldEdit Edit(double x, double y, double radius, List<ShapePoint>? shape) =>
        HoldEdit.FromEditorState(x, y, radius, null, HoldCategory.Hand, false, shape, null, null, null, flagBouldersOnMove: false);

    private static async Task SetPocketAsync(WallTestHarness h, Guid holdId)
    {
        await using var db = h.CreateContext();
        var hold = await db.Holds.SingleAsync(x => x.Id == holdId);
        hold.X = 0.1;
        hold.Y = 0.1;
        hold.Radius = 0.02;
        hold.ShapePoints = ShapePoint.DefaultOctagon(0.02);
        hold.ShapeHoles = Ring();
        hold.OutlineSource = HoldOutlineSource.AutoContour;
        await db.SaveChangesAsync();
    }

    private static async Task<Guid> SeedStagedPocketAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        var panel = new WallPanel
        {
            WallId = h.WallId, Col = 0, Row = 0, StagedPhoto = [1, 2, 3], StagedPhotoContentType = "image/jpeg", Generation = 1,
        };
        var hold = new Hold
        {
            WallId = h.WallId, WallPanelId = panel.Id, X = 0.5, Y = 0.5, Radius = 0.02, Generation = 1,
            ShapePoints = ShapePoint.DefaultOctagon(0.02), ShapeHoles = Ring(),
        };
        db.WallPanels.Add(panel);
        db.Holds.Add(hold);
        await db.SaveChangesAsync();
        return hold.Id;
    }

    private static async Task<Hold> LoadAsync(WallTestHarness h, Guid holdId)
    {
        await using var db = h.CreateContext();
        return await db.Holds.AsNoTracking().SingleAsync(x => x.Id == holdId);
    }
}
