// <copyright file="GlyphGeometryPersistenceTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Round-trips the experimental glyph wall-geometry model through a real provider: the new wall,
/// segment and hold columns, the two new tables, and the "one active geometry model per wall" index.
/// Every context is created fresh from the harness factory (own connection) — never shared.
/// </summary>
public class GlyphGeometryPersistenceTests
{
    [Fact]
    public async Task NewColumns_DefaultToOffAndNull()
    {
        using var harness = new WallTestHarness();
        var holds = await harness.SeedWallAsync(holdCount: 1);

        await using var db = harness.CreateContext();
        var wall = await db.Walls.SingleAsync(w => w.Id == harness.WallId);
        var hold = await db.Holds.SingleAsync(h => h.Id == holds[0].Id);

        Assert.False(wall.GlyphsEnabled);
        Assert.Null(wall.MarkerSizeMm);
        Assert.Null(hold.WidthMm);
        Assert.Null(hold.PlaneAMm);
        Assert.Null(hold.OutlineSource);
        Assert.Null(hold.MetricSource);
    }

    [Fact]
    public async Task WallSegmentAndHoldGlyphColumns_RoundTrip()
    {
        using var harness = new WallTestHarness();
        var holds = await harness.SeedWallAsync(holdCount: 1);
        var segmentId = await SeedGlyphColumnsAsync(harness, holds[0].Id);

        await using var db = harness.CreateContext();
        var wall = await db.Walls.SingleAsync(w => w.Id == harness.WallId);
        Assert.True(wall.GlyphsEnabled);
        Assert.Equal(125.0, wall.MarkerSizeMm);

        var segment = await db.WallSegments.SingleAsync(s => s.Id == segmentId);
        Assert.Equal(45, segment.Angle);
        Assert.Equal(0, segment.MarkerSegmentIndex);
        Assert.Equal(44.6, segment.MeasuredAngle);
        Assert.Equal(-1.5, segment.MeasuredYaw);

        var hold = await db.Holds.SingleAsync(h => h.Id == holds[0].Id);
        Assert.Equal(0.1, hold.X);
        Assert.Equal(82.5, hold.WidthMm);
        Assert.Equal(61.0, hold.HeightMm);
        Assert.Equal(3950.0, hold.AreaMm2);
        Assert.Equal("5a", hold.FacetId);
        Assert.Equal(1234.5, hold.PlaneAMm);
        Assert.Equal(-87.25, hold.PlaneBMm);
        Assert.Equal("{\"hue\":42}", hold.FingerprintJson);
        Assert.Equal(HoldOutlineSource.AutoContour, hold.OutlineSource);
        Assert.Equal("multi-marker", hold.MetricSource);
    }

    [Fact]
    public async Task GeometryModelAndMarkerObservation_RoundTrip()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync(holdCount: 0);
        var panelId = await AddPanelAsync(harness);
        var userId = Guid.NewGuid();

        await using (var db = harness.CreateContext())
        {
            db.WallGeometryModels.Add(new WallGeometryModel
            {
                WallId = harness.WallId,
                Json = "{\"version\":1,\"units\":\"mm\"}",
                SchemaVersion = 1,
                Source = "glyph-solver v1",
                CreatedByUserId = userId,
                IsActive = true,
                WidthMm = 4200,
                HeightMm = 3100,
                ReprojRmsPx = 0.9,
                Notes = "capture 1",
            });
            db.WallMarkerObservations.Add(new WallMarkerObservation
            {
                WallPanelId = panelId,
                PanelGeneration = 1,
                FromStagedPhoto = true,
                MarkerId = 33,
                CornersJson = "[[0.1,0.1],[0.2,0.1],[0.2,0.2],[0.1,0.2]]",
                SidePx = 96.1,
                Synthetic = true,
            });
            await db.SaveChangesAsync();
        }

        await using var read = harness.CreateContext();
        var model = await read.WallGeometryModels.SingleAsync(m => m.WallId == harness.WallId);
        Assert.Equal("{\"version\":1,\"units\":\"mm\"}", model.Json);
        Assert.Equal(1, model.SchemaVersion);
        Assert.Equal("glyph-solver v1", model.Source);
        Assert.Equal(userId, model.CreatedByUserId);
        Assert.True(model.IsActive);
        Assert.Equal(4200, model.WidthMm);
        Assert.Equal(3100, model.HeightMm);
        Assert.Equal(0.9, model.ReprojRmsPx);
        Assert.Equal("capture 1", model.Notes);

        var observation = await read.WallMarkerObservations.SingleAsync(o => o.WallPanelId == panelId);
        Assert.Equal(1, observation.PanelGeneration);
        Assert.True(observation.FromStagedPhoto);
        Assert.Equal(33, observation.MarkerId);
        Assert.Equal("[[0.1,0.1],[0.2,0.1],[0.2,0.2],[0.1,0.2]]", observation.CornersJson);
        Assert.Equal(96.1, observation.SidePx);
        Assert.True(observation.Synthetic);
    }

    [Fact]
    public async Task SecondActiveGeometryModel_IsRejected_ButHistoryIsNot()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync(holdCount: 0);

        await using (var db = harness.CreateContext())
        {
            db.WallGeometryModels.Add(Model(harness.WallId, isActive: false));
            db.WallGeometryModels.Add(Model(harness.WallId, isActive: false));
            db.WallGeometryModels.Add(Model(harness.WallId, isActive: true));
            await db.SaveChangesAsync();
        }

        await using (var db = harness.CreateContext())
        {
            db.WallGeometryModels.Add(Model(harness.WallId, isActive: true));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        await using var read = harness.CreateContext();
        Assert.Equal(3, await read.WallGeometryModels.CountAsync(m => m.WallId == harness.WallId));
        Assert.Equal(1, await read.WallGeometryModels.CountAsync(m => m.WallId == harness.WallId && m.IsActive));
    }

    [Fact]
    public async Task DeletingPanel_CascadesItsMarkerObservations()
    {
        using var harness = new WallTestHarness();
        await harness.SeedWallAsync(holdCount: 0);
        var panelId = await AddPanelAsync(harness);

        await using (var db = harness.CreateContext())
        {
            db.WallMarkerObservations.Add(new WallMarkerObservation
            {
                WallPanelId = panelId,
                PanelGeneration = 1,
                MarkerId = 0,
                CornersJson = "[]",
                SidePx = 50,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = harness.CreateContext())
        {
            db.WallPanels.Remove(await db.WallPanels.SingleAsync(p => p.Id == panelId));
            await db.SaveChangesAsync();
        }

        await using var read = harness.CreateContext();
        Assert.False(await read.WallMarkerObservations.AnyAsync());
    }

    private static WallGeometryModel Model(Guid wallId, bool isActive) => new()
    {
        WallId = wallId,
        Json = "{}",
        SchemaVersion = 1,
        Source = "glyph-solver v1",
        IsActive = isActive,
    };

    private static async Task<Guid> AddPanelAsync(WallTestHarness harness)
    {
        await using var db = harness.CreateContext();
        var panel = new WallPanel { WallId = harness.WallId, Col = 0, Row = 0, Generation = 1 };
        db.WallPanels.Add(panel);
        await db.SaveChangesAsync();
        return panel.Id;
    }

    private static async Task<Guid> SeedGlyphColumnsAsync(WallTestHarness harness, Guid holdId)
    {
        await using var db = harness.CreateContext();
        var wall = await db.Walls.SingleAsync(w => w.Id == harness.WallId);
        wall.GlyphsEnabled = true;
        wall.MarkerSizeMm = 125.0;

        var segment = new WallSegment
        {
            WallId = wall.Id,
            Name = "main wall",
            Angle = 45,
            MarkerSegmentIndex = 0,
            MeasuredAngle = 44.6,
            MeasuredYaw = -1.5,
        };
        db.WallSegments.Add(segment);

        var hold = await db.Holds.SingleAsync(h => h.Id == holdId);
        hold.WidthMm = 82.5;
        hold.HeightMm = 61.0;
        hold.AreaMm2 = 3950.0;
        hold.FacetId = "5a";
        hold.PlaneAMm = 1234.5;
        hold.PlaneBMm = -87.25;
        hold.FingerprintJson = "{\"hue\":42}";
        hold.OutlineSource = HoldOutlineSource.AutoContour;
        hold.MetricSource = "multi-marker";

        await db.SaveChangesAsync();
        return segment.Id;
    }
}
