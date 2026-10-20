// <copyright file="HybridZoomPlannerTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.Footprints;
using Blocwerk.Core.Geometry.HybridZoom;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Services.HybridZoom;
using SkiaSharp;
using static Blocwerk.Core.Tests.HybridZoomScene;

namespace Blocwerk.Core.Tests;

/// <summary>The hybrid zoom's plan (panel → facet through the placed holds, then the photo choice) and its render.</summary>
public class HybridZoomPlannerTests
{
    internal static readonly Guid PanelId = Guid.NewGuid();

    private static readonly WallGeometryDocument Doc = new()
    {
        Version = 1,
        Segments = [new WallGeometrySegment { Index = 0, Facets = [new WallGeometryFacet { Id = "0", Origin = [0, 0, 0], U = [1, 0, 0], V = [0, 0, 1], Normal = [0, -1, 0] }] }],
    };

    [Fact]
    public void Plan_PicksTheCloseUp_ThroughTheHoldFittedPanelMapping()
    {
        var inputs = Inputs(PlacedHolds(), LookAt("p01", [1000, -1600, 1000], Target), LookAt("p02", [1000, -4800, 1000], Target));

        var (status, plan, _) = HybridZoomPlanner.Plan(inputs, PanelId, View, new CloseUpRules(), 0.25);

        Assert.Equal(CloseUpStatus.Rendered, status);
        Assert.Equal("p01", plan!.Choice.Name);
        Assert.Equal("p01.jpg", plan.StoredPath);
        Assert.Equal(View.X, plan.Rect.X, 9);
        Assert.Equal(View.W, plan.Rect.W, 9);
        Assert.Equal(5000.0 / 1600, plan.Choice.Gain, 1);
    }

    [Fact]
    public void Plan_ShrinksAPaddedView_UntilAPhotoCoversIt()
    {
        // A close camera with a narrow field: covers the middle of the padded request but not all of it.
        var narrow = LookAt("p01", [1000, -700, 1000], Target);
        var wide = ViewRect.Create(0.42, 0.42, 0.16, 0.16)!.Value;

        var (status, plan, _) = HybridZoomPlanner.Plan(Inputs(PlacedHolds(), narrow), PanelId, wide, new CloseUpRules(), 0.25);

        Assert.Equal(CloseUpStatus.Rendered, status);
        Assert.True(plan!.Rect.W < wide.W);
        Assert.Equal(wide.CentreX, plan.Rect.CentreX, 6);
    }

    [Fact]
    public void Plan_WithoutPlacedHolds_HasNoMapping()
    {
        var (status, plan, _) = HybridZoomPlanner.Plan(
            Inputs([], LookAt("p01", [1000, -1600, 1000], Target)), PanelId, View, new CloseUpRules(), 0.25);

        Assert.Equal(CloseUpStatus.NoMapping, status);
        Assert.Null(plan);
    }

    [Fact]
    public void Plan_OnAVolume_DefersToTheVolumeView()
    {
        var id = Guid.NewGuid();
        var inputs = Inputs(PlacedHolds(), LookAt("p01", [1000, -1600, 1000], Target)) with
        {
            Volumes = new Dictionary<string, List<VolumeOutline>>
            {
                ["0"] = [new VolumeOutline(id, [(500, 500), (1500, 500), (1500, 1500), (500, 1500)], HeightMm: 320)],
            },
        };

        var (status, _, volume) = HybridZoomPlanner.Plan(inputs, PanelId, View, new CloseUpRules(), 0.25);

        Assert.Equal(CloseUpStatus.OnVolume, status);
        Assert.Equal(id, volume!.Id);
        Assert.Equal("0:1000:1000:320:under", volume.ToUnderQuery());
    }

    [Fact]
    public void Render_WarpsThePhotoOntoTheView_ThroughAScaledDecode()
    {
        // 1600 × 1200 photo: left half red, right half blue. The view shows x 400..1200, y 200..1000 of it.
        var jpeg = TwoTonePhoto(1600, 1200);
        var h = PlaneHomography.FromCoefficients([800, 0, 400, 0, 800, 200, 0, 0, 1]);
        var choice = new CloseUpChoice("p01", h, (400, 200, 1200, 1000), 2, 3, 1, 0, 6);
        var plan = new ZoomPlan(choice, View, "p01.jpg", 1600);

        var size = HybridZoomRenderer.OutputSize(choice, 200, 1.0);
        var output = HybridZoomRenderer.Render(jpeg, plan, size, 90)!.Image;

        using var bitmap = SKBitmap.Decode(output);
        Assert.Equal((200, 200), (bitmap.Width, bitmap.Height));
        var left = bitmap.GetPixel(40, 100);
        var right = bitmap.GetPixel(160, 100);
        Assert.True(left.Red > 200 && left.Blue < 60, $"left {left}");
        Assert.True(right.Blue > 200 && right.Red < 60, $"right {right}");
    }

    [Fact]
    public void OutputSize_NeverExceedsTheCropsOwnPixels()
    {
        var choice = new CloseUpChoice("p01", PlaneHomography.FromCoefficients([1, 0, 0, 0, 1, 0, 0, 0, 1]), (0, 0, 300, 150), 2, 3, 1, 0, 6);

        Assert.Equal((300, 150), HybridZoomRenderer.OutputSize(choice, 2048, 2.0));
        Assert.Equal((100, 200), HybridZoomRenderer.OutputSize(choice, 200, 0.5));
    }

    /// <summary>Twelve holds spread over the panel, placed exactly where the panel camera sees them.</summary>
    internal static List<Hold> PlacedHolds()
    {
        var holds = new List<Hold>();
        for (var i = 0; i < 12; i++)
        {
            double a = 200 + ((i % 4) * 500), b = 300 + ((i / 4) * 600);
            var px = Panel.Project(HybridZoomScene.Wall.ToWorld(a, b))!.Value;
            holds.Add(new Hold
            {
                Id = Guid.NewGuid(), WallPanelId = PanelId, Generation = 1, X = px.X / Width, Y = px.Y / Height,
                FacetId = "0", PlaneAMm = a, PlaneBMm = b,
            });
        }

        return holds;
    }

    internal static ZoomInputs Inputs(IEnumerable<Hold> holds, params SolvedCamera[] cameras)
    {
        var model = new ZoomModel(
            Doc,
            new Dictionary<string, FacetFrame> { ["0"] = HybridZoomScene.Wall },
            new Dictionary<string, PlaneRectMm>(),
            cameras.ToDictionary(c => c.Image, StringComparer.OrdinalIgnoreCase));
        var photos = cameras.ToDictionary(c => c.Image, c => new ZoomSourcePhoto(c.Image + ".jpg", Width, Height, null), StringComparer.OrdinalIgnoreCase);
        var projector = HoldPlaneProjector.Create(holds, Doc, null);
        return new ZoomInputs(model, photos, projector, 1, new PanelPhotoInfo(Width, Height, null), new Dictionary<string, List<VolumeOutline>>());
    }

    internal static byte[] TwoTonePhoto(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            using var red = new SKPaint { Color = SKColors.Red };
            using var blue = new SKPaint { Color = SKColors.Blue };
            canvas.DrawRect(0, 0, width / 2f, height, red);
            canvas.DrawRect(width / 2f, 0, width / 2f, height, blue);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 95);
        return data.ToArray();
    }
}
