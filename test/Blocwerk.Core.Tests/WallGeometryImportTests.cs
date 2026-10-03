using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Importing and activating <c>wall-geometry.json</c> models: summary columns, the "one active model
/// per wall" swap in a single SaveChanges, and the measured angles copied onto bound segments.
/// Every context comes fresh from the harness factory (own connection).
/// </summary>
public class WallGeometryImportTests
{
    [Fact]
    public async Task Import_StoresAnActiveModel_WithItsSummary()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);

        var result = await WallGlyphSettingsTests.Service(h).ImportGeometryAsync(h.WallId, GlyphGeometryJson.Build(), "  first solve ");

        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        await using var db = h.CreateContext();
        var model = await db.WallGeometryModels.SingleAsync();
        Assert.True(model.IsActive);
        Assert.Equal(1, model.SchemaVersion);
        Assert.Equal(0.9, model.ReprojRmsPx);
        Assert.Equal(3100, model.WidthMm);
        Assert.Equal(2600, model.HeightMm);
        Assert.Equal("first solve", model.Notes);
        Assert.Equal(h.Owner.Id, model.CreatedByUserId);

        // The wall had no marker size yet, so it adopts the document's.
        Assert.Equal(125, (await db.Walls.SingleAsync(w => w.Id == h.WallId)).MarkerSizeMm);
    }

    [Fact]
    public async Task Import_KeepsAMarkerSizeTheWallAlreadyDeclared()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var glyphs = WallGlyphSettingsTests.Service(h);
        await glyphs.SetGlyphSettingsAsync(h.WallId, enabled: true, markerSizeMm: 150);

        await glyphs.ImportGeometryAsync(h.WallId, GlyphGeometryJson.Build(markerSizeMm: 125), notes: null);

        await using var db = h.CreateContext();
        Assert.Equal(150, (await db.Walls.SingleAsync(w => w.Id == h.WallId)).MarkerSizeMm);
    }

    [Fact]
    public async Task ReImport_LeavesExactlyOneActiveModel_TheNewest()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var glyphs = WallGlyphSettingsTests.Service(h);

        var first = await glyphs.ImportGeometryAsync(h.WallId, GlyphGeometryJson.Build(reprojRmsPx: 1.5), null);
        var second = await glyphs.ImportGeometryAsync(h.WallId, GlyphGeometryJson.Build(reprojRmsPx: 0.7), null);
        var third = await glyphs.ImportGeometryAsync(h.WallId, GlyphGeometryJson.Build(reprojRmsPx: 0.5), null);

        Assert.All(new[] { first, second, third }, r => Assert.True(r.Succeeded));
        await using var db = h.CreateContext();
        var active = await db.WallGeometryModels.Where(m => m.IsActive).ToListAsync();
        Assert.Equal(3, await db.WallGeometryModels.CountAsync());
        Assert.Equal(third.Model!.Id, Assert.Single(active).Id);

        var history = await glyphs.GetGeometryHistoryAsync(h.WallId);
        Assert.Equal(3, history.Count);
        Assert.Single(history, e => e.IsActive);
    }

    [Fact]
    public async Task ActivatingAnOlderModel_SwapsTheActiveOne_AndReappliesItsAngles()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var segmentId = await AddSegmentAsync(h, "Main", markerIndex: 0);
        var glyphs = WallGlyphSettingsTests.Service(h);
        var older = await glyphs.ImportGeometryAsync(h.WallId, GlyphGeometryJson.Build(seg0MeasuredAngle: 40, seg0Yaw: 1), null);
        var newer = await glyphs.ImportGeometryAsync(h.WallId, GlyphGeometryJson.Build(seg0MeasuredAngle: 44, seg0Yaw: 2), null);

        await glyphs.ActivateGeometryAsync(older.Model!.Id);

        await using var db = h.CreateContext();
        Assert.True((await db.WallGeometryModels.SingleAsync(m => m.Id == older.Model.Id)).IsActive);
        Assert.False((await db.WallGeometryModels.SingleAsync(m => m.Id == newer.Model!.Id)).IsActive);
        var segment = await db.WallSegments.SingleAsync(s => s.Id == segmentId);
        Assert.Equal(40, segment.MeasuredAngle);
        Assert.Equal(1, segment.MeasuredYaw);
    }

    [Fact]
    public async Task Activation_NeverTripsTheOneActiveIndex_WhateverOrderTheIdsSortIn()
    {
        // EF orders same-table UPDATEs by primary key, so a single-SaveChanges swap fails whenever the
        // model being activated sorts before the one being retired. Cycling through several random ids
        // hits both orders.
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var glyphs = WallGlyphSettingsTests.Service(h);
        var ids = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            ids.Add((await glyphs.ImportGeometryAsync(h.WallId, GlyphGeometryJson.Build(), null)).Model!.Id);
        }

        foreach (var id in ids.Concat(Enumerable.Reverse(ids)))
        {
            await glyphs.ActivateGeometryAsync(id);

            await using var db = h.CreateContext();
            Assert.Equal(id, (await db.WallGeometryModels.SingleAsync(m => m.IsActive)).Id);
        }
    }

    [Fact]
    public async Task Import_CopiesMeasuredAngles_OntoMatchingSegmentsOnly()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var main = await AddSegmentAsync(h, "Main", markerIndex: 0);
        var folded = await AddSegmentAsync(h, "Right", markerIndex: 5);
        var unbound = await AddSegmentAsync(h, "Slab", markerIndex: null, measured: 9);
        var missing = await AddSegmentAsync(h, "Roof", markerIndex: 7, measured: 9);

        await WallGlyphSettingsTests.Service(h).ImportGeometryAsync(h.WallId, GlyphGeometryJson.Build(seg0MeasuredAngle: 44.6, seg0Yaw: 0), null);

        await using var db = h.CreateContext();
        var rows = await db.WallSegments.ToDictionaryAsync(s => s.Id);
        Assert.Equal(44.6, rows[main].MeasuredAngle);
        Assert.Equal(0, rows[main].MeasuredYaw);
        Assert.Equal(45, rows[main].Angle); // the declared angle is never overwritten

        // Folded segment 5 has no segment-level angle; its primary (first) facet "5a" supplies both.
        Assert.Equal(12.5, rows[folded].MeasuredAngle);
        Assert.Equal(36.9, rows[folded].MeasuredYaw);

        Assert.Equal(9, rows[unbound].MeasuredAngle);
        Assert.Null(rows[missing].MeasuredAngle);
        Assert.Null(rows[missing].MeasuredYaw);
    }

    [Fact]
    public async Task BindingASegment_TakesTheActiveModelsValues_AndRefusesADuplicateBinding()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var first = await AddSegmentAsync(h, "Main", markerIndex: null);
        var second = await AddSegmentAsync(h, "Other", markerIndex: null);
        var glyphs = WallGlyphSettingsTests.Service(h);
        await glyphs.ImportGeometryAsync(h.WallId, GlyphGeometryJson.Build(seg0MeasuredAngle: 44.6), null);

        await glyphs.SetSegmentMarkerIndexAsync(first, 0);
        await Assert.ThrowsAsync<InvalidOperationException>(() => glyphs.SetSegmentMarkerIndexAsync(second, 0));

        await using (var db = h.CreateContext())
        {
            var bound = await db.WallSegments.SingleAsync(s => s.Id == first);
            Assert.Equal(0, bound.MarkerSegmentIndex);
            Assert.Equal(44.6, bound.MeasuredAngle);
            Assert.Null((await db.WallSegments.SingleAsync(s => s.Id == second)).MarkerSegmentIndex);
        }

        await glyphs.SetSegmentMarkerIndexAsync(first, null);
        await using var read = h.CreateContext();
        var unbound = await read.WallSegments.SingleAsync(s => s.Id == first);
        Assert.Null(unbound.MarkerSegmentIndex);
        Assert.Null(unbound.MeasuredAngle);
    }

    [Fact]
    public async Task ActiveGeometry_ListsEveryFacet()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var glyphs = WallGlyphSettingsTests.Service(h);
        Assert.Null(await glyphs.GetActiveGeometryAsync(h.WallId));

        await glyphs.ImportGeometryAsync(h.WallId, GlyphGeometryJson.Build(), null);
        var active = await glyphs.GetActiveGeometryAsync(h.WallId);

        Assert.NotNull(active);
        Assert.Equal(2, active.MarkerCount);
        Assert.Equal(["0", "5a", "5b"], active.Facets.Select(f => f.FacetId));
        Assert.Equal(45.0, active.Facets[0].DeclaredAngleDeg);
        Assert.Equal(1, active.Facets[1].MarkerCount);
    }

    private static async Task<Guid> AddSegmentAsync(WallTestHarness h, string name, int? markerIndex, double? measured = null)
    {
        await using var db = h.CreateContext();
        var segment = new WallSegment
        {
            WallId = h.WallId,
            Name = name,
            Angle = 45,
            MarkerSegmentIndex = markerIndex,
            MeasuredAngle = measured,
            MeasuredYaw = measured,
        };
        db.WallSegments.Add(segment);
        await db.SaveChangesAsync();
        return segment.Id;
    }
}
