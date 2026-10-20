// <copyright file="HybridZoomSelectionTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.HybridZoom;
using static Blocwerk.Core.Tests.HybridZoomScene;

namespace Blocwerk.Core.Tests;

/// <summary>Which capture photo the hybrid zoom shows for a zoomed panel view, and the warp that lays it over the panel.</summary>
public class HybridZoomSelectionTests
{
    private static readonly CloseUpRules Rules = new();

    [Fact]
    public void PanelResolution_IsFocalOverDistance()
    {
        Assert.Equal(Focal / 5000, PanelPxPerMm(View), 3);
    }

    [Fact]
    public void FrontalCloseCamera_WinsOverObliqueAndFar_WithItsResolutionGain()
    {
        var frontal = LookAt("p01", [1000, -1600, 1000], Target);
        var oblique = LookAt("p02", [1000 + (1600 * Math.Sin(0.87)), -1600 * Math.Cos(0.87), 1000], Target);
        var far = LookAt("p03", [1000, -5000, 1000], Target);

        var ranked = Rank(Photo(oblique), Photo(far), Photo(frontal));

        Assert.Equal(new[] { "p01", "p02" }, ranked.Select(c => c.Name));
        Assert.Equal(5000.0 / 1600, ranked[0].Gain, 0.001);
        Assert.Equal(1.0, ranked[0].Frontality, 3);
        Assert.True(ranked[1].Frontality < 0.7);
    }

    [Fact]
    public void CameraBehindTheWall_IsNeverChosen()
    {
        var behind = LookAt("p01", [1000, 1600, 1000], Target);

        Assert.Empty(Rank(Photo(behind)));
    }

    [Fact]
    public void CameraThatDoesNotSeeTheWholeView_IsSkipped()
    {
        var aside = LookAt("p01", [4000, -1600, 1000], [4000, 0, 1000]);

        Assert.Empty(Rank(Photo(aside)));
    }

    [Fact]
    public void NoGainOverThePanel_ShowsNothing()
    {
        var sameDistance = LookAt("p01", [1100, -5000, 900], [1100, 0, 900]);

        Assert.Empty(Rank(Photo(sameDistance)));
    }

    [Fact]
    public void OfTwoEquivalentViews_TheSharperPhotoWins()
    {
        var a = LookAt("p01", [1000, -1600, 1000], Target);
        var b = LookAt("p02", [1000, -1600, 1000], Target);

        var ranked = Rank(Photo(a, sharpness: 40), Photo(b, sharpness: 120));

        Assert.Equal("p02", ranked[0].Name);
    }

    [Fact]
    public void Warp_LandsEverySampleWhereTheLensPutsIt_EvenWithDistortion()
    {
        var camera = LookAt("p01", [1000, -1600, 1000], Target, dist: [0.08, -0.02, 0, 0, 0]);

        var choice = Assert.Single(Rank(Photo(camera)));

        // A strong barrel (k1 = 0.08) bends a 1250 px crop by about a pixel: far below the 1 % the selector allows.
        var b = choice.PhotoBounds;
        Assert.True(choice.ResidualPx < 0.002 * Math.Max(b.X1 - b.X0, b.Y1 - b.Y0), $"residual {choice.ResidualPx}");
        foreach (var (u, v) in new[] { (0.0, 0.0), (0.25, 0.75), (1.0, 1.0), (0.5, 0.5) })
        {
            var expected = camera.Project(WorldAt(View)(u, v)!)!.Value;
            var (x, y) = choice.ViewToPhoto.Apply(u, v);
            Assert.True(Math.Abs(expected.X - x) <= choice.ResidualPx + 1e-6 && Math.Abs(expected.Y - y) <= choice.ResidualPx + 1e-6);
        }
    }

    [Fact]
    public void UnmappableView_ShowsNothing()
    {
        var frontal = LookAt("p01", [1000, -1600, 1000], Target);

        var ranked = CloseUpCameraSelector.Rank((_, _) => null, Wall.Normal, 0.6, [Photo(frontal)], Rules);

        Assert.Empty(ranked);
    }

    [Fact]
    public void DecodedToOutput_InvertsOutputToDecodedPhoto()
    {
        var viewToPhoto = PlaneHomography.FromCoefficients([400, 30, 200, -20, 380, 100, 0.0001, 0.0002, 1]);
        const int W = 300, H = 200;
        const double Scale = 0.25;

        var m = CloseUpWarp.DecodedToOutput(viewToPhoto, W, H, Scale)!;
        var toOutput = PlaneHomography.FromCoefficients(m);

        var (px, py) = viewToPhoto.Apply(75.0 / W, 150.0 / H);
        var (ox, oy) = toOutput.Apply(px * Scale, py * Scale);
        Assert.Equal(75, ox, 6);
        Assert.Equal(150, oy, 6);
    }

    [Fact]
    public void ViewRect_IsClampedSnappedOutwardAndScaledAboutItsCentre()
    {
        Assert.Null(ViewRect.Create(0.5, 0.5, 0.001, 0.1));
        Assert.Null(ViewRect.Create(double.NaN, 0, 0.1, 0.1));
        var clamped = ViewRect.Create(0.9, -0.1, 0.3, 0.3)!.Value;
        Assert.Equal((0.9, 0.0, 0.1, 0.2), (Math.Round(clamped.X, 9), Math.Round(clamped.Y, 9), Math.Round(clamped.W, 9), Math.Round(clamped.H, 9)));

        var r = ViewRect.Create(0.1234, 0.5678, 0.0333, 0.0444)!.Value;
        var s = r.Snapped();
        Assert.True(s.X <= r.X && s.Y <= r.Y && s.X + s.W >= r.X + r.W && s.Y + s.H >= r.Y + r.H);
        Assert.Equal(0, (s.X * ViewRect.Grid) % 1, 9);

        var half = r.Scaled(0.5);
        Assert.Equal(r.CentreX, half.CentreX, 9);
        Assert.Equal(r.W / 2, half.W, 9);
    }

    [Fact]
    public void VolumeOverlap_CountsTheSamplesOnTheVolume()
    {
        var id = Guid.NewGuid();
        var square = new VolumeOutline(id, [(0, 0), (100, 0), (100, 100), (0, 100)]);
        (double A, double B)[] samples = [(50, 50), (10, 90), (150, 50), (-5, 5)];

        var (volume, share) = VolumeOverlap.Of(samples, [square]);

        Assert.Equal(id, volume);
        Assert.Equal(0.5, share, 9);
        Assert.Equal((null, 0.0), VolumeOverlap.Of(samples, []));
    }

    private static IReadOnlyList<CloseUpChoice> Rank(params CloseUpPhoto[] photos) =>
        CloseUpCameraSelector.Rank(WorldAt(View), Wall.Normal, PanelPxPerMm(View), photos, Rules);
}
