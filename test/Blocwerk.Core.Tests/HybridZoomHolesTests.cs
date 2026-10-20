// <copyright file="HybridZoomHolesTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.HybridZoom;
using Blocwerk.Core.Geometry.Proposals;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Services.HybridZoom;
using SkiaSharp;
using static Blocwerk.Core.Tests.HybridZoomScene;

namespace Blocwerk.Core.Tests;

/// <summary>Volumes inside a zoomed view are cut out of the planar close-up, so the panel photo shows there.</summary>
public class HybridZoomHolesTests
{
    [Fact]
    public void ConvexHull_DropsInteriorPoints_CounterClockwise()
    {
        (double U, double V)[] points = [(0, 0), (1, 0), (0.5, 0.5), (1, 1), (0, 1), (0.2, 0.8)];

        var hull = CloseUpHoles.ConvexHull(points);

        Assert.Equal(new[] { (0.0, 0.0), (1.0, 0.0), (1.0, 1.0), (0.0, 1.0) }, hull);
    }

    [Fact]
    public void Hole_CoversTheFootprint_AndTheVolumeTopTheCloseUpSees()
    {
        // A 200 mm square volume, 300 mm deep, left of the close camera: its top is seen shifted further left.
        var camera = LookAt("p01", [1000, -1600, 1000], Target);
        var volume = new VolumeOutline(Guid.NewGuid(), [(800, 900), (1000, 900), (1000, 1100), (800, 1100)], HeightMm: 300);
        var view = ViewRect.Create(0.4, 0.4, 0.2, 0.2)!.Value;
        var choice = Assert.Single(CloseUpCameraSelector.Rank(WorldAt(view), HybridZoomScene.Wall.Normal, PanelPxPerMm(view), [Photo(camera)], new()));

        var hole = Assert.Single(CloseUpHoles.For([volume], HybridZoomScene.Wall, camera, choice.ViewToPhoto));

        var toView = choice.ViewToPhoto.Inverse()!;
        (double U, double V) ToView(double[] world)
        {
            var px = camera.Project(world)!.Value;
            return toView.Apply(px.X, px.Y);
        }

        var ring = hole.Select(p => (p.U, p.V)).ToList();
        var centre = ToView(HybridZoomScene.Wall.ToWorld(900, 1000));
        var top = ToView(HybridZoomScene.Wall.ToWorld(800, 1100, 300));
        Assert.True(VolumeOverlap.Contains(ring, centre.U, centre.V));
        Assert.True(VolumeOverlap.Contains(ring, top.U, top.V));
    }

    [Fact]
    public void Render_WithAHole_IsWebPAndTransparentThere()
    {
        var jpeg = HybridZoomPlannerTests.TwoTonePhoto(1600, 1200);
        var h = PlaneHomography.FromCoefficients([800, 0, 400, 0, 800, 200, 0, 0, 1]);
        var choice = new CloseUpChoice("p01", h, (400, 200, 1200, 1000), 2, 3, 1, 0, 6);
        IReadOnlyList<(double U, double V)> square = [(0.1, 0.1), (0.4, 0.1), (0.4, 0.4), (0.1, 0.4)];
        var plan = new ZoomPlan(choice, View, "p01.jpg", 1600, [square]);

        var output = HybridZoomRenderer.Render(jpeg, plan, (200, 200), 90)!.Image;

        Assert.Equal("image/webp", HybridZoomRenderer.ContentTypeOf(plan));
        using var codec = SKCodec.Create(new MemoryStream(output));
        Assert.Equal(SKEncodedImageFormat.Webp, codec.EncodedFormat);
        using var bitmap = SKBitmap.Decode(output);
        Assert.True(bitmap.GetPixel(50, 50).Alpha < 10, $"inside {bitmap.GetPixel(50, 50)}");
        Assert.Equal(255, bitmap.GetPixel(150, 150).Alpha);
    }

    [Fact]
    public void FacetMask_CutsTheCellsWhereTheCloseUpSeesAnotherFacet()
    {
        // The main wall, and a board 300 mm in front of it covering a ≥ 1000 mm: the right half of the view.
        var camera = LookAt("p01", [1000, -1600, 1000], Target);
        var view = ViewRect.Create(0.4, 0.4, 0.2, 0.2)!.Value;
        var choice = Assert.Single(CloseUpCameraSelector.Rank(WorldAt(view), HybridZoomScene.Wall.Normal, PanelPxPerMm(view), [Photo(camera)], new()));
        var board = FacetFrame.From(new WallGeometryFacet { Id = "k", Origin = [0, -300, 0], U = [1, 0, 0], V = [0, 0, 1], Normal = [0, -1, 0] })!;
        CastFacet[] facets =
        [
            new("0", HybridZoomScene.Wall, new PlaneRectMm(-5000, 5000, -5000, 5000), []),
            new("k", board, new PlaneRectMm(1000, 5000, -5000, 5000), []),
        ];

        var holes = CloseUpFacetMask.OffFacet(camera, choice.ViewToPhoto, "0", facets);

        Assert.Equal(CloseUpFacetMask.Steps, holes.Count);
        Assert.All(holes, h => Assert.InRange(h[0].U, 0.4, 0.6));
        Assert.All(holes, h => Assert.Equal(1.0, h[1].U, 9));
        Assert.Empty(CloseUpFacetMask.OffFacet(camera, choice.ViewToPhoto, "0", [facets[0]]));
    }

    [Fact]
    public void Plan_CutsASmallVolumeOutOfTheCloseUp_AndNamesItForTheLookUnderneath()
    {
        var id = Guid.NewGuid();
        var inputs = HybridZoomPlannerTests.Inputs(HybridZoomPlannerTests.PlacedHolds(), LookAt("p01", [1000, -1600, 1000], Target)) with
        {
            Volumes = new Dictionary<string, List<VolumeOutline>>
            {
                ["0"] = [new VolumeOutline(id, [(950, 950), (1050, 950), (1050, 1050), (950, 1050)], HeightMm: 150)],
            },
        };

        var (status, plan, volume) = HybridZoomPlanner.Plan(inputs, HybridZoomPlannerTests.PanelId, View, new CloseUpRules(), 0.5);

        Assert.Equal(CloseUpStatus.Rendered, status);
        Assert.Single(plan!.Holes!);
        Assert.Equal(id, volume!.Id);
        Assert.Equal("image/webp", HybridZoomRenderer.ContentTypeOf(plan));
    }
}
