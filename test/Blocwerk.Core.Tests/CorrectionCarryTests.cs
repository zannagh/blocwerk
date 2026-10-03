// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Capture.Corrections;
using Blocwerk.Core.Capture.FollowUp;
using Blocwerk.Core.Detection.Enrichment;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Geometry.TextureRegistration;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Geometry.Volumes;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A correction carries its parent's derived data (<see cref="CorrectionCarry"/>): the placements, shapes, volumes and
/// proposals are mapped by the similarity, the follow-ups keep them instead of registering the photos again, and
/// re-activating the parent restores its data exactly (its own runs stay revertable).
/// </summary>
public class CorrectionCarryTests
{
    /// <summary>Two points 1800 mm apart in the model that are 1980 mm apart on the wall: ×1.1 about the origin.</summary>
    private static readonly CaptureScaleReference TenPercent = new(1, [1000, 750], [1600, 750], 1980);

    [Fact]
    public async Task MakeSizesExact_MapsThePlacementsShapesVolumesAndProposals_AndMarksTheFollowUpsKept()
    {
        using var h = new WallTestHarness();
        var w = await CarryWall.SeedAsync(h);

        var result = await GeometryCorrectionFixture.Service(h, new CorrectionFollowUpQueue()).MakeSizesExactAsync(h.WallId, TenPercent);

        await using var db = h.CreateContext();
        var hold = await db.Holds.SingleAsync(x => x.Id == w.OnWall);
        Assert.Equal(("0", HoldMetric.TextureRegistration), (hold.FacetId, hold.MetricSource));
        Assert.Equal(1100, hold.PlaneAMm!.Value, 9);
        Assert.Equal(440, hold.PlaneBMm!.Value, 9);
        Assert.Equal(44, hold.WidthMm!.Value, 9);
        Assert.Equal(1089, hold.AreaMm2!.Value, 9);
        var footprint = JsonSerializer.Deserialize<HoldFootprint>(hold.FootprintMm!, JsonSerializerOptions.Web)!;
        Assert.Equal(-11, footprint.Outline[0][0], 9);
        Assert.Equal(1.1, footprint.ShiftA, 9);
        Assert.Equal(33, JsonSerializer.Deserialize<HoldProtrusion>(hold.ProtrusionMm!, JsonSerializerOptions.Web)!.HeightMm, 9);

        var volume = await db.WallVolumes.SingleAsync(v => v.GeometryModelId == result.ModelId);
        Assert.Equal(165, volume.HeightMm, 9);
        Assert.Equal(11, VolumeSurface.FromJson(volume.SurfaceJson)!.Grid.CellMm, 9);
        var placement = HoldVolumePlacement.FromJson(hold.VolumePlacementJson)!;
        Assert.Equal(volume.Id, placement.VolumeId);
        Assert.Equal(1105.5, placement.A, 9);
        Assert.Equal(110, placement.H, 9);
        Assert.Equal(1100, placement.FromA, 9);

        var proposal = await db.HoldProposals.SingleAsync();
        Assert.Equal(110, proposal.A, 9);
        Assert.Equal(110, proposal.X, 9);
        Assert.Equal(220, proposal.Z, 9);
        Assert.Equal(result.ModelId, proposal.GeometryModelId);

        var run = await db.HoldPlacementRuns.SingleAsync(r => r.GeometryModelId == result.ModelId);
        Assert.Equal((HoldPlacementTrigger.Correction, 2, 0), (run.Trigger, run.PlacedCount, run.FailedCount));
        var record = CaptureFollowUpRecord.Parse((await db.WallCaptures.SingleAsync()).FollowUpJson);
        Assert.Equal(w.ModelId, record.CarriedFrom);
    }

    [Fact]
    public async Task ReactivatingTheParent_RestoresItsDataExactly_AndItsOwnRunStaysRevertable()
    {
        using var h = new WallTestHarness();
        var w = await CarryWall.SeedAsync(h);
        var before = await Snapshot(h);
        var queue = new CorrectionFollowUpQueue();
        var result = await GeometryCorrectionFixture.Service(h, queue).MakeSizesExactAsync(h.WallId, TenPercent);

        await Glyphs(h, queue).ActivateGeometryAsync(w.ModelId);

        Assert.Equal(before, await Snapshot(h));
        await using (var db = h.CreateContext())
        {
            Assert.NotNull((await db.HoldPlacementRuns.SingleAsync(r => r.GeometryModelId == result.ModelId)).RevertedAt);
            var proposal = await db.HoldProposals.SingleAsync();
            Assert.Equal(100, proposal.A, 9);
            Assert.Equal(100, proposal.X, 9);
            Assert.Equal(w.ModelId, proposal.GeometryModelId);
            Assert.Equal(result.ModelId, CaptureFollowUpRecord.Parse((await db.WallCaptures.SingleAsync()).FollowUpJson).CarriedFrom);
        }

        var placement = new HoldTexturePlacementService(h.DbContextFactory, h.CurrentUser, NullLogger<HoldTexturePlacementService>.Instance);
        Assert.Equal(1, (await placement.RevertAsync(h.WallId, w.ParentRunId)).Reverted);

        // And forward again: the correction's placements come back.
        await Glyphs(h, queue).ActivateGeometryAsync(result.ModelId);
        await using var check = h.CreateContext();
        Assert.Equal(1100, (await check.Holds.SingleAsync(x => x.Id == w.OnWall)).PlaneAMm!.Value, 9);
    }

    [Fact]
    public async Task NotPartOfTheWall_LeavesTheDroppedFacetsHoldsUnmeasured_UntilTheParentIsBack()
    {
        using var h = new WallTestHarness();
        var w = await CarryWall.SeedAsync(h);
        var before = await Snapshot(h);
        var queue = new CorrectionFollowUpQueue();

        var result = await GeometryCorrectionFixture.Service(h, queue).DropSurfaceAsync(h.WallId, "1");

        await using (var db = h.CreateContext())
        {
            var dropped = await db.Holds.SingleAsync(x => x.Id == w.OnSide);
            Assert.Equal((null, null, HoldMetric.TextureRegistrationRejected), (dropped.FacetId, dropped.FootprintMm, dropped.MetricSource));
            Assert.Equal(1000, (await db.Holds.SingleAsync(x => x.Id == w.OnWall)).PlaneAMm!.Value, 9);
            var run = await db.HoldPlacementRuns.SingleAsync(r => r.GeometryModelId == result.ModelId);
            Assert.Equal((1, 1), (run.PlacedCount, run.FailedCount));
        }

        await Glyphs(h, queue).ActivateGeometryAsync(w.ModelId);
        Assert.Equal(before, await Snapshot(h));
    }

    [Fact]
    public async Task CarryFromParent_UndoesTheVersionsOwnRegistration_AndMapsTheParentsPlacements()
    {
        using var h = new WallTestHarness();
        var w = await CarryWall.SeedAsync(h);
        var service = GeometryCorrectionFixture.Service(h, new CorrectionFollowUpQueue());
        var result = await service.MakeSizesExactAsync(h.WallId, TenPercent);
        await CarryWall.RegisterBadlyAsync(h, result.ModelId, w.OnWall);

        var carried = await service.CarryFromParentAsync(h.WallId);

        Assert.Equal(2, carried.Placed);
        await using var db = h.CreateContext();
        Assert.Equal(1100, (await db.Holds.SingleAsync(x => x.Id == w.OnWall)).PlaneAMm!.Value, 9);
        Assert.Equal(2, await db.HoldPlacementRuns.CountAsync(r => r.GeometryModelId == result.ModelId && r.RevertedAt != null));
        await Glyphs(h, new CorrectionFollowUpQueue()).ActivateGeometryAsync(w.ModelId);
        await GeometryCorrectionTests.Refused(() => service.CarryFromParentAsync(h.WallId), "not a corrected version");
    }

    [Fact]
    public void TheChain_KeepsTheCarriedSteps_ButRunsTheCoverageReport_AndAnythingAlreadyRecorded()
    {
        var carried = CaptureFollowUpRecord.Carried(Guid.NewGuid());
        var place = new PlaceHoldsFollowUpStep(null!);
        var coverage = new CoverageReportFollowUpStep(null!, null!);

        Assert.True(CaptureFollowUpChain.IsKept(place, carried));
        Assert.False(CaptureFollowUpChain.IsKept(coverage, carried));
        Assert.False(CaptureFollowUpChain.IsKept(place, CaptureFollowUpRecord.Empty));
        var recorded = carried.With(new CaptureFollowUpEntry(place.Key, CaptureFollowUpOutcome.Done, string.Empty, DateTimeOffset.UtcNow));
        Assert.False(CaptureFollowUpChain.IsKept(place, recorded));
        Assert.Equal(carried.CarriedFrom, CaptureFollowUpRecord.Parse(recorded.ToJson()).CarriedFrom);
    }

    private static WallGlyphService Glyphs(WallTestHarness h, CorrectionFollowUpQueue queue) =>
        new(h.DbContextFactory, h.CurrentUser, NullLogger<WallGlyphService>.Instance, null, queue);

    /// <summary>Every hold's derived fields, as text (so "restored exactly" means bit for bit).</summary>
    private static async Task<string> Snapshot(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        var holds = await db.Holds.AsNoTracking().OrderBy(x => x.X).ToListAsync();
        return string.Join('\n', holds.Select(x => JsonSerializer.Serialize(new
        {
            x.FacetId, x.PlaneAMm, x.PlaneBMm, x.MetricSource, x.WidthMm, x.HeightMm, x.AreaMm2,
            x.FingerprintJson, x.FootprintMm, x.ProtrusionMm, x.VolumePlacementJson,
        })));
    }
}
