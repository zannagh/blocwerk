// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Detection.Enrichment;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Geometry.TextureRegistration;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Geometry.Volumes;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The correction fixture's wall (<see cref="GeometryCorrectionFixture"/>) with data derived on its model: a hold on facet
/// "0" at (1000, 400) with sizes, footprint, protrusion and a placement on a volume, a hold on facet "1" at (200, 300),
/// the volume (facet "0", 150 mm high), a pending hold proposal at world (100, 0, 200), and the placement run that
/// placed the first hold on this model.
/// </summary>
/// <param name="ModelId">The active model.</param>
/// <param name="OnWall">The hold on facet "0".</param>
/// <param name="OnSide">The hold on facet "1".</param>
/// <param name="ParentRunId">The run that placed <paramref name="OnWall"/>.</param>
internal sealed record CarryWall(Guid ModelId, Guid OnWall, Guid OnSide, Guid ParentRunId)
{
    /// <summary>Two cells of 10 mm, 100 and 200 mm high (int16 little-endian, base64).</summary>
    private const string Surface = """{"version":1,"aLo":0,"bLo":0,"cellMm":10,"cols":2,"rows":1,"heights":"ZADIAA=="}""";

    public static async Task<CarryWall> SeedAsync(WallTestHarness h)
    {
        var (modelId, _) = await GeometryCorrectionFixture.SeedAsync(h);
        await using var db = h.CreateContext();
        var volume = new WallVolume
        {
            WallId = h.WallId, GeometryModelId = modelId, FacetId = "0", Index = 1, FootprintJson = "[[0,0],[100,0],[100,100]]",
            SurfaceJson = Surface, AreaM2 = 0.01, HeightMm = 150, Confidence = 0.8,
        };
        var onWall = Placed(h.WallId, 0.3, "0", 1000, 400, volume.Id);
        var onSide = Placed(h.WallId, 0.6, "1", 200, 300, null);
        db.WallVolumes.Add(volume);
        db.Holds.AddRange(onWall, onSide);
        db.HoldProposals.Add(new HoldProposal
        {
            WallId = h.WallId, GeometryModelId = modelId, FacetId = "0", A = 100, B = 200, H = 10, X = 100, Y = 0, Z = 200, SizeMm = 40,
            Views = 2, Confidence = 0.5, BestPhoto = "p01", Status = HoldProposalStatus.Pending,
        });
        var entry = HoldPlacementEntry.Before(onWall) with { PlacementHash = HoldPlacementEntry.HashPlacement(onWall) };
        var run = new HoldPlacementRun
        {
            WallId = h.WallId, GeometryModelId = modelId, CreatedByUserId = h.Owner.Id, Trigger = "capture",
            HoldsJson = HoldPlacementEntry.ToJson([entry]), PlacedCount = 1,
        };
        db.HoldPlacementRuns.Add(run);
        await db.SaveChangesAsync();
        return new CarryWall(modelId, onWall.Id, onSide.Id, run.Id);
    }

    /// <summary>What the follow-up registration wrote on a corrected version before corrections carried: another position, on a run.</summary>
    public static async Task RegisterBadlyAsync(WallTestHarness h, Guid modelId, Guid holdId)
    {
        await using var db = h.CreateContext();
        var hold = await db.Holds.SingleAsync(x => x.Id == holdId);
        var entry = HoldPlacementEntry.Before(hold);
        hold.PlaneAMm = 1234;
        db.HoldPlacementRuns.Add(new HoldPlacementRun
        {
            WallId = h.WallId, GeometryModelId = modelId, CreatedByUserId = h.Owner.Id, Trigger = "capture",
            HoldsJson = HoldPlacementEntry.ToJson([entry with { PlacementHash = HoldPlacementEntry.HashPlacement(hold) }]), PlacedCount = 1,
        });
        await db.SaveChangesAsync();
    }

    private static Hold Placed(Guid wallId, double x, string facet, double a, double b, Guid? volumeId) => new()
    {
        WallId = wallId,
        X = x,
        Y = 0.4,
        Radius = 0.02,
        FacetId = facet,
        PlaneAMm = a,
        PlaneBMm = b,
        MetricSource = HoldMetric.TextureRegistration,
        WidthMm = 40,
        HeightMm = 30,
        AreaMm2 = 900,
        FootprintMm = new HoldFootprint(HoldFootprintSource.MultiView, 3, 20, null, "k", [[-10, -5], [10, -5], [0, 8]], 1, 2).ToJson(),
        ProtrusionMm = new HoldProtrusion(HoldProtrusionSource.Splat, 50, 0, 30, 1, 2, 40, "k").ToJson(),
        VolumePlacementJson = volumeId is { } v ? new HoldVolumePlacement(v, a + 5, b + 5, 100, [0, 0, 1], a, b, "panel").ToJson() : null,
    };
}
