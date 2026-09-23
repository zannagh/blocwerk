// <copyright file="HoldEnrichmentMarkerHoldTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The printed marker sheets are flat squares, never holds: on a marker wall the enrichment takes the
/// auto-detected holds sitting on one (its quad grown by 15 %) back out before the caller saves. Manual and
/// virtual holds, and every hold on a wall without markers, stay.
/// </summary>
public class HoldEnrichmentMarkerHoldTests
{
    // Marker 0 covers 0.1..0.2 on both axes; grown by 15 % it reaches 0.0925..0.2075.
    private static readonly (double X, double Y) OnMarker = (0.15, 0.15);
    private static readonly (double X, double Y) OnMarkerMargin = (0.205, 0.15);
    private static readonly (double X, double Y) BesideMarker = (0.215, 0.15);
    private static readonly (double X, double Y) FarAway = (0.5, 0.5);

    [Fact]
    public async Task MarkerWall_DropsAutoHoldsInsideAMarkerQuad()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);

        var (summary, stored) = await EnrichAsync(h, glyphs: true, OnMarker, OnMarkerMargin, BesideMarker, FarAway);

        Assert.Equal(2, summary.DroppedMarkerHolds.Count);
        Assert.Equal([BesideMarker, FarAway], stored.Select(x => (x.X, x.Y)).Order());
        Assert.All(stored, x => Assert.NotNull(x.OutlineSource));
    }

    [Fact]
    public async Task WallWithoutMarkers_KeepsEveryHold()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);

        var (summary, stored) = await EnrichAsync(h, glyphs: false, OnMarker, OnMarkerMargin, FarAway);

        Assert.Empty(summary.DroppedMarkerHolds);
        Assert.Equal(3, stored.Count);
    }

    [Fact]
    public async Task ManualAndVirtualHoldsInsideAMarker_AreKept()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        await using (var db = h.CreateContext())
        {
            var wall = await db.Walls.SingleAsync(w => w.Id == h.WallId);
            wall.GlyphsEnabled = true;
            var manual = new Hold { WallId = h.WallId, X = OnMarker.X, Y = OnMarker.Y, Radius = 0.02 };
            var virtualHold = new Hold { WallId = h.WallId, X = OnMarker.X, Y = OnMarker.Y, IsVirtual = true, IsAutoDetected = true };
            db.Holds.AddRange(manual, virtualHold);

            var summary = await Service().EnrichAsync(db, new HoldEnrichmentRequest([1], wall, [manual, virtualHold]));
            await db.SaveChangesAsync();

            Assert.Empty(summary.DroppedMarkerHolds);
        }

        await using var read = h.CreateContext();
        Assert.Equal(2, await read.Holds.CountAsync());
    }

    // Through an ingest choke point: the redetect saves only the real hold and reports it alone.
    [Fact]
    public async Task Redetect_OnMarkerWall_SavesNoMarkerSheetHold()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        Guid panelId;
        await using (var db = h.CreateContext())
        {
            (await db.Walls.SingleAsync(w => w.Id == h.WallId)).GlyphsEnabled = true;
            var panel = new WallPanel { WallId = h.WallId, Col = 0, Row = 0, Photo = [1, 2, 3] };
            db.WallPanels.Add(panel);
            await db.SaveChangesAsync();
            panelId = panel.Id;
        }

        h.HoldDetection.DetectHoldsAsync(Arg.Any<byte[]>(), Arg.Any<HoldDetectionParameters?>())
            .Returns(_ => Task.FromResult(new List<DetectedHold>
            {
                new(OnMarker.X, OnMarker.Y, 0.02, null, 0.9),
                new(FarAway.X, FarAway.Y, 0.02, null, 0.9),
            }));
        var panels = new WallPanelService(
            h.DbContextFactory, h.CurrentUser, h.HoldDetection, Substitute.For<IHoldOverlapMatcher>(),
            NullLogger<WallPanelService>.Instance, holdEnrichment: Service());

        Assert.Equal(1, await panels.RedetectPanelHoldsAsync(h.WallId, panelId));

        await using var check = h.CreateContext();
        var only = await check.Holds.SingleAsync(x => x.WallPanelId == panelId);
        Assert.Equal(FarAway, (only.X, only.Y));
    }

    private static IHoldEnrichmentService Service() => EnrichmentFakes.Service(
        EnrichmentFakes.Outlines(), EnrichmentFakes.Markers(EnrichmentFakes.Square(0, 100, 100, 100)));

    private static async Task<(HoldEnrichmentSummary Summary, List<Hold> Stored)> EnrichAsync(
        WallTestHarness h, bool glyphs, params (double X, double Y)[] positions)
    {
        HoldEnrichmentSummary summary;
        await using (var db = h.CreateContext())
        {
            var wall = await db.Walls.SingleAsync(w => w.Id == h.WallId);
            wall.GlyphsEnabled = glyphs;
            var holds = positions.Select(p => EnrichmentFakes.AutoHold(h.WallId, p.X, p.Y)).ToList();
            db.Holds.AddRange(holds);
            summary = await Service().EnrichAsync(db, new HoldEnrichmentRequest([1], wall, holds));
            await db.SaveChangesAsync();
        }

        await using var read = h.CreateContext();
        return (summary, await read.Holds.ToListAsync());
    }
}
