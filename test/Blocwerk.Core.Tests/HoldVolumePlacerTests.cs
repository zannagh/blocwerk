// <copyright file="HoldVolumePlacerTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.Footprints;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Geometry.Volumes;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A hold on the synthetic pyramid (<see cref="VolumeDetectorTests"/>) was mapped FLAT: where the panel
/// camera's ray through it meets the facet plane. The placer must walk that ray back onto the volume, and the
/// 3D view must draw the hold there only while the placement is current.
/// </summary>
public class HoldVolumePlacerTests
{
    private static readonly FacetFrame Wall = HoldFootprintEstimatorTests.Wall;

    [Fact]
    public void FlatHold_IsWalkedBackOntoTheVolume()
    {
        var volume = new PlacedVolume(Guid.NewGuid(), "0", DetectedSurface());
        var truth = (A: 1420.0, B: 960.0, H: VolumeDetectorTests.PyramidAt(1420, 960));
        double[] camera = [300, -2500, 400];
        var flat = FlatOf(camera, truth);

        var p = HoldVolumePlacer.Place(Wall, flat.A, flat.B, [volume], camera, [], 15)!;

        Assert.Equal(volume.Id, p.VolumeId);
        Assert.Equal("panel", p.Camera);
        Assert.InRange(Math.Sqrt(((p.A - truth.A) * (p.A - truth.A)) + ((p.B - truth.B) * (p.B - truth.B)) + ((p.H - truth.H) * (p.H - truth.H))), 0, 20);
        Assert.True(p.Matches(flat.A, flat.B));
        Assert.False(p.Matches(flat.A + 10, flat.B));
    }

    [Fact]
    public void HoldBesideTheVolume_StaysOnTheFacet()
    {
        var volume = new PlacedVolume(Guid.NewGuid(), "0", DetectedSurface());

        Assert.Null(HoldVolumePlacer.Place(Wall, 600, 1500, [volume], [300, -2500, 400], [], 15));
    }

    [Fact]
    public void WithoutAPanelCamera_TheMostFrontalCaptureCameraStandsIn()
    {
        var volume = new PlacedVolume(Guid.NewGuid(), "0", DetectedSurface());
        double[] frontal = [1400, -2500, 950];
        double[] grazing = [4000, -600, 950];

        var p = HoldVolumePlacer.Place(Wall, 1400, 950, [volume], null, [grazing, frontal], 15)!;

        Assert.Equal("frontal", p.Camera);
        Assert.InRange(p.H, 100, 135);
    }

    [Fact]
    public void View_DrawsCurrentPlacementsOnTheVolume()
    {
        var surface = DetectedSurface();
        var row = new WallVolume { FacetId = "0", FootprintJson = "[[1200,800],[1600,800],[1600,1100],[1200,1100]]", SurfaceJson = surface.ToJson(), Index = 1 };
        var placement = new HoldVolumePlacement(row.Id, 1400, 950, 118, [0, 0, 1], 1450, 950, "panel");
        var on = new Hold { FacetId = "0", PlaneAMm = 1450, PlaneBMm = 950, VolumePlacementJson = placement.ToJson() };
        var moved = new Hold { FacetId = "0", PlaneAMm = 1480, PlaneBMm = 950, VolumePlacementJson = placement.ToJson() };
        var view = new Wall3DView
        {
            Facets = [new Wall3DFacet("0", 0, "wall", Wall.Origin, Wall.U, Wall.V, Wall.Normal, [], VolumeDetectorTests.Extent, 90)],
            Holds = [Drawn(on), Drawn(moved)],
        };

        var result = Wall3DVolumes.Apply(view, [row], [on, moved], new Dictionary<string, TextureSourceMap>(), []);

        Assert.Single(result.Volumes);
        var drawnOn = result.Holds.Single(h => h.Id == on.Id).Protrusion!;
        Assert.Equal(row.Id, drawnOn.VolumeId);
        Assert.Equal(-50, drawnOn.ShiftA);
        Assert.Equal(118, drawnOn.BaseMm);
        Assert.Null(result.Holds.Single(h => h.Id == moved.Id).Protrusion?.VolumeId);
    }

    /// <summary>The pyramid as the detector shapes it from the synthetic scene.</summary>
    private static VolumeSurface DetectedSurface() =>
        VolumeDetector.Detect(
            VolumeDetectorTests.Scene(withMacro: false),
            new Dictionary<string, FacetFrame> { ["0"] = Wall },
            new Dictionary<string, PlaneRectMm> { ["0"] = VolumeDetectorTests.Extent },
            new Dictionary<string, List<KnownHoldEllipse>>()).Single(v => v.IsAccepted).Surface!;

    /// <summary>Where the ray from <paramref name="camera"/> through the true point meets the facet plane.</summary>
    private static (double A, double B) FlatOf(double[] camera, (double A, double B, double H) truth)
    {
        var c = FacetCloud.Local(Wall, camera[0], camera[1], camera[2]);
        var t = c.H / (c.H - truth.H);
        return (c.A + ((truth.A - c.A) * t), c.B + ((truth.B - c.B) * t));
    }

    private static Wall3DHold Drawn(Hold h) =>
        new(h.Id, "0", Wall.ToWorld(h.PlaneAMm!.Value, h.PlaneBMm!.Value), h.PlaneAMm!.Value, h.PlaneBMm!.Value, 60, 60, true, null, "x", "#fff", false, 0, null,
            Protrusion: new Wall3DHoldProtrusion(0, 25, 0, 0, 30, true, false));
}
