// <copyright file="HoldFootprintRefinerTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.Footprints;
using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <see cref="HoldFootprintRefiner"/> on one panel photo of <see cref="PanelCameraEstimatorTests"/>' synthetic camera:
/// the in-view ones of 90 holds placed consistently on the main facet (so the photo's hold-fit mapping and panel camera are known) and,
/// like the carried holds of a partly re-registered photo, holds whose placement disagrees with that mapping by a metre.
/// No capture photos: every footprint is the single-view correction of the panel silhouette.
/// </summary>
public class HoldFootprintRefinerTests
{
    private static readonly Guid PanelId = Guid.NewGuid();

    private static readonly WallGeometryDocument Doc = new()
    {
        Segments =
        [
            new WallGeometrySegment
            {
                Index = 0,
                Facets = [new WallGeometryFacet { Id = "0", Origin = [0, 0, 0], U = [1, 0, 0], V = [0, 0, 1], Normal = [0, -1, 0] }],
            },
        ],
    };

    [Fact]
    public void AHoldPlacedAwayFromThePhotosMapping_GetsItsFootprintAtItsPlacement()
    {
        var live = Placed();
        var twin = live[7];
        var carried = Hold(twin.X, twin.Y, twin.PlaneAMm!.Value + 1100, twin.PlaneBMm!.Value + 900, 0.004);
        live.Add(carried);

        var result = Refine(live);

        var fp = result.Footprints[carried.Id];
        Assert.True(HoldFootprintGuard.OffsetMm(fp) < 30, $"footprint centroid {HoldFootprintGuard.OffsetMm(fp):F0} mm off its placement");
        Assert.Empty(result.Guarded!);
    }

    [Fact]
    public void AFootprintFarFromItsPlacement_IsNotProduced_AndReported()
    {
        var live = Placed();
        var stray = Hold(0.5, 0.5, 2000, 1000, 0.004);

        // The traced outline lies entirely to one side of the hold's centre (most of a metre away on the facet).
        stray.ShapePoints = ShapePoint.DefaultOctagon(0.004).Select(p => new ShapePoint { Dx = p.Dx + 0.12, Dy = p.Dy }).ToList();
        live.Add(stray);

        var result = Refine(live);

        Assert.False(result.Footprints.ContainsKey(stray.Id));
        var guarded = Assert.Single(result.Guarded!);
        Assert.Equal(stray.Id, guarded.HoldId);
        Assert.False(guarded.FellBack);
        Assert.True(guarded.OffsetMm > HoldFootprintGuard.MinLimitMm);
        Assert.All(live.Where(h => h != stray), h => Assert.True(HoldFootprintGuard.Near(result.Footprints[h.Id], h)));
    }

    [Fact]
    public void TheGuardsLimit_GrowsWithTheHoldsSize()
    {
        var fp = new HoldFootprint(HoldFootprintSource.MultiView, 3, 30, null, "k", [[70, -10], [110, -10], [110, 30], [70, 30]]);

        Assert.Equal(90.6, HoldFootprintGuard.OffsetMm(fp), 1);
        Assert.Equal(60, HoldFootprintGuard.LimitMm(new Hold()));
        Assert.False(HoldFootprintGuard.Near(fp, new Hold { WidthMm = 80, HeightMm = 40 }));
        Assert.True(HoldFootprintGuard.Near(fp, new Hold { WidthMm = 40, HeightMm = 130 }));
    }

    private static HoldFootprintRefinement Refine(List<Hold> live)
    {
        var projector = HoldPlaneProjector.Create(live, Doc, null);
        var photos = new Dictionary<Wall3DPhotoKey, PanelPhotoInfo>
        {
            [new Wall3DPhotoKey(PanelId, 0)] = new(PanelCameraEstimatorTests.Width, PanelCameraEstimatorTests.Height, PanelCameraEstimatorTests.Focal),
        };
        var result = HoldFootprintRefiner.Refine(live, Doc, [], _ => null, projector, null, photos);
        Assert.NotNull(result.PanelCameras![new Wall3DPhotoKey(PanelId, 0)].Centre);
        return result;
    }

    private static List<Hold> Placed() =>
        PanelCameraEstimatorTests.Holds(PanelCameraEstimatorTests.Centre, PanelCameraEstimatorTests.LookAt, "0", 90)
            .Select(p => Hold(p.X, p.Y, p.A, p.B, 0.004))
            .ToList();

    private static Hold Hold(double x, double y, double a, double b, double radius) => new()
    {
        Id = Guid.NewGuid(),
        WallPanelId = PanelId,
        X = x,
        Y = y,
        FacetId = "0",
        PlaneAMm = a,
        PlaneBMm = b,
        ShapePoints = ShapePoint.DefaultOctagon(radius),
    };
}
