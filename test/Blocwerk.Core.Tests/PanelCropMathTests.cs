// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Services.PanelCrop;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The crop math on its own: a hold inside the crop keeps its spot on the wall (the same pixel of the original photo),
/// its outline follows, a crop of a crop composes into one crop of the original, and the cut-off rule.
/// </summary>
public class PanelCropMathTests
{
    private const double Tolerance = 1e-12;

    [Fact]
    public void HoldInsideTheCrop_KeepsItsWallPosition()
    {
        var rect = new PanelCropRect(0.2, 0.1, 0.5, 0.6);
        var hold = new Hold { X = 0.4, Y = 0.55, Radius = 0.02 };

        var mapped = PanelFrameMap.IntoCrop(rect).Mapped(hold);

        // The same point of the original photo: the crop's origin plus the new fraction of the crop's size.
        Assert.Equal(hold.X, rect.Left + (mapped.X * rect.Width), Tolerance);
        Assert.Equal(hold.Y, rect.Top + (mapped.Y * rect.Height), Tolerance);
        Assert.Equal(0.4, mapped.X, Tolerance);
        Assert.Equal(0.75, mapped.Y, Tolerance);
    }

    [Fact]
    public void Outline_IsRemappedPerAxis_AndEveryVertexKeepsItsWallPosition()
    {
        var rect = new PanelCropRect(0.25, 0.0, 0.5, 0.8);
        var hold = new Hold
        {
            X = 0.5, Y = 0.5, Radius = 0.03,
            ShapePoints = [new() { Dx = -0.02, Dy = -0.01 }, new() { Dx = 0.03, Dy = 0 }, new() { Dx = 0, Dy = 0.04 }],
            ShapeHoles = [[new() { Dx = 0.005, Dy = 0.005 }, new() { Dx = 0.01, Dy = 0.005 }, new() { Dx = 0.005, Dy = 0.01 }]],
        };

        var mapped = PanelFrameMap.IntoCrop(rect).Mapped(hold);

        for (var i = 0; i < hold.ShapePoints.Count; i++)
        {
            var before = (X: hold.X + hold.ShapePoints[i].Dx, Y: hold.Y + hold.ShapePoints[i].Dy);
            var after = (X: mapped.X + mapped.ShapePoints![i].Dx, Y: mapped.Y + mapped.ShapePoints[i].Dy);
            Assert.Equal(before.X, rect.Left + (after.X * rect.Width), Tolerance);
            Assert.Equal(before.Y, rect.Top + (after.Y * rect.Height), Tolerance);
        }

        Assert.Equal(0.01, mapped.ShapeHoles![0][0].Dx, Tolerance);
        Assert.Equal(0.00625, mapped.ShapeHoles[0][0].Dy, Tolerance);
        Assert.Equal(0.03 / Math.Sqrt(0.5 * 0.8), mapped.Radius, Tolerance);
    }

    [Fact]
    public void CropThenUndo_RoundTrips()
    {
        var rect = new PanelCropRect(0.13, 0.27, 0.61, 0.52);
        var hold = new Hold { X = 0.33, Y = 0.41, Radius = 0.017, ShapePoints = ShapePoint.DefaultOctagon(0.017) };
        var copy = hold.Clone();

        PanelFrameMap.IntoCrop(rect).Apply(copy);
        PanelFrameMap.OutOfCrop(rect).Apply(copy);

        Assert.Equal(hold.X, copy.X, Tolerance);
        Assert.Equal(hold.Y, copy.Y, Tolerance);
        Assert.Equal(hold.Radius, copy.Radius, Tolerance);
        Assert.All(hold.ShapePoints!.Zip(copy.ShapePoints!), p => Assert.Equal(p.First.Dx, p.Second.Dx, Tolerance));
    }

    [Fact]
    public void CropOfACrop_ComposesIntoOneCropOfTheOriginal()
    {
        var first = new PanelCropRect(0.1, 0.2, 0.8, 0.5);
        var second = new PanelCropRect(0.25, 0.1, 0.5, 0.8);
        var composed = second.Within(first);
        var (x, y) = (0.42, 0.37);

        var twice = PanelFrameMap.IntoCrop(first).Then(PanelFrameMap.IntoCrop(second)).Point(x, y);
        var once = PanelFrameMap.IntoCrop(composed).Point(x, y);

        Assert.Equal(0.3, composed.Left, Tolerance);
        Assert.Equal(0.25, composed.Top, Tolerance);
        Assert.Equal(0.4, composed.Width, Tolerance);
        Assert.Equal(0.4, composed.Height, Tolerance);
        Assert.Equal(once.X, twice.X, Tolerance);
        Assert.Equal(once.Y, twice.Y, Tolerance);
    }

    [Theory]
    [InlineData(1.02, 0.5, 0.01, true)] // wholly outside, just right of the frame
    [InlineData(-0.5, 0.5, 0.05, true)] // wholly outside, far left
    [InlineData(1.04, 0.5, 0.05, false)] // centre outside, the circle still reaches in
    [InlineData(-0.04, 0.5, 0.05, false)] // centre outside on the left, still reaches in
    [InlineData(1.05, 0.5, 0.05, false)] // circle just touching the edge (the 16-gon has a vertex on it)
    [InlineData(0.99, 0.5, 0.05, false)] // centre inside, a sliver outside
    [InlineData(0.99, 0.99, 0.2, false)] // centre in a corner, most of the circle outside but some inside
    [InlineData(0.5, 0.5, 0.05, false)] // well inside
    [InlineData(1.0, 1.0, 0.0001, false)] // a speck exactly on the corner
    public void CutOff_OnlyWhenTheWholeCircleIsOutside(double x, double y, double radius, bool expected)
    {
        var hold = new Hold { X = x, Y = y, Radius = radius };

        Assert.Equal(expected, PanelCropCutoff.IsCutOff(hold));
    }

    [Fact]
    public void CutOff_KeepsATracedOutline_ThatCrossesTheEdge_WithItsCentreOutside()
    {
        var hold = new Hold
        {
            X = 1.03, Y = 0.5, Radius = 0.01,
            ShapePoints = [new() { Dx = -0.08, Dy = -0.02 }, new() { Dx = 0.05, Dy = -0.02 }, new() { Dx = 0.05, Dy = 0.02 }, new() { Dx = -0.08, Dy = 0.02 }],
        };

        Assert.False(PanelCropCutoff.IsCutOff(hold));
    }

    [Fact]
    public void CutOff_RemovesATracedOutline_ThatIsWhollyOutside()
    {
        var hold = new Hold
        {
            X = 1.2, Y = 0.5, Radius = 0.01,
            ShapePoints = [new() { Dx = -0.08, Dy = -0.02 }, new() { Dx = 0.05, Dy = -0.02 }, new() { Dx = 0.05, Dy = 0.02 }, new() { Dx = -0.08, Dy = 0.02 }],
        };

        Assert.True(PanelCropCutoff.IsCutOff(hold));
    }

    [Fact]
    public void CutOff_KeepsAnOutline_ThatSpansTheWholeFrame_WithNoVertexInside()
    {
        var hold = new Hold
        {
            X = 0.5, Y = 0.5, Radius = 0.01,
            ShapePoints = [new() { Dx = -2, Dy = -2 }, new() { Dx = 2, Dy = -2 }, new() { Dx = 2, Dy = 2 }, new() { Dx = -2, Dy = 2 }],
        };

        Assert.False(PanelCropCutoff.IsCutOff(hold));
    }

    [Theory]
    [InlineData(0, 0, 1, 1)] // nothing to crop
    [InlineData(0.5, 0, 0.6, 1)] // outside the photo
    [InlineData(0, 0, 0.05, 1)] // too thin
    [InlineData(double.NaN, 0, 0.5, 0.5)]
    public void Validate_RefusesUselessRectangles(double left, double top, double width, double height)
    {
        Assert.Throws<ArgumentException>(() => new PanelCropRect(left, top, width, height).Validate());
    }
}
