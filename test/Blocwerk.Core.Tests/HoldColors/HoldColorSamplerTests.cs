// <copyright file="HoldColorSamplerTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Holds.Colors;

namespace Blocwerk.Core.Tests.HoldColors;

/// <summary>Which pixels the colour estimate reads: the hold's outline (not just its centre), minus pockets and rim.</summary>
public class HoldColorSamplerTests
{
    private static readonly OklabColor Wood = OklabColor.FromRgb(222, 196, 160);
    private static readonly OklabColor Blue = OklabColor.FromRgb(40, 90, 200);
    private static readonly OklabColor Chalk = OklabColor.FromRgb(236, 234, 230);

    [Fact]
    public void ACircleHold_IsReadFromItsCore_AndTheWallAroundIt()
    {
        var hold = new Hold { X = 0.5, Y = 0.5, Radius = 0.1 };

        // The blue hold fills 70% of its circle; the rim of the circle is wall.
        var estimate = HoldColorSampler.Estimate(hold, 400, 300, (x, y) => Distance(x, y, 200, 150) <= 28 ? Blue : Wood);

        Assert.Equal("blue", estimate.Color);
        Assert.True(estimate.Area > 0);
    }

    [Fact]
    public void AChalkedCentre_DoesNotDecide_TheWholeOutlineDoes()
    {
        var hold = new Hold { X = 0.5, Y = 0.5, Radius = 0.1 };

        // Chalk exactly where a centre sample would land.
        var estimate = HoldColorSampler.Estimate(
            hold, 400, 300, (x, y) => Distance(x, y, 200, 150) <= 8 ? Chalk : Distance(x, y, 200, 150) <= 40 ? Blue : Wood);

        Assert.Equal("blue", estimate.Color);
    }

    [Fact]
    public void ATracedOutline_IsUsedInsteadOfTheCircle_AndItsPocketIsLeftOut()
    {
        // A wide bar: its bounding circle is mostly wall, its outline is all hold. The pocket shows the wall.
        var hold = new Hold
        {
            X = 0.5,
            Y = 0.5,
            Radius = 0.2,
            ShapePoints = Ring(0.2, 0.04),
            ShapeHoles = [Ring(0.03, 0.02)],
        };
        bool InBar(int x, int y) => Math.Abs(x - 200) <= 80 && Math.Abs(y - 150) <= 12;
        bool InPocket(int x, int y) => Math.Abs(x - 200) <= 12 && Math.Abs(y - 150) <= 6;

        var estimate = HoldColorSampler.Estimate(hold, 400, 300, (x, y) => InBar(x, y) && !InPocket(x, y) ? Blue : Wood);

        Assert.Equal("blue", estimate.Color);
    }

    [Fact]
    public void AHoldAtThePhotoEdge_ReadsOnlyPixelsThatExist_AndALargeHoldIsSampledSparsely()
    {
        var atEdge = new Hold { X = 0.0, Y = 0.0, Radius = 0.1 };
        var large = new Hold { X = 0.5, Y = 0.5, Radius = 0.3 };
        var reads = 0;

        var edge = HoldColorSampler.Estimate(atEdge, 400, 300, (x, y) => x < 0 || y < 0 || x >= 400 || y >= 300
            ? throw new InvalidOperationException()
            : Distance(x, y, 0, 0) <= 30 ? Blue : Wood);
        var big = HoldColorSampler.Estimate(large, 4000, 3000, (x, y) =>
        {
            reads++;
            return Distance(x, y, 2000, 1500) <= 1000 ? Blue : Wood;
        });

        Assert.Equal("blue", edge.Color);
        Assert.Equal("blue", big.Color);
        Assert.InRange(reads, 500, 10_000);
    }

    [Fact]
    public void ARealPhotoFile_IsDecodedAndRead()
    {
        var photo = HoldColorTestPhoto.Build((0.25, 0.5, 0.06, HoldColorTestPhoto.Red), (0.75, 0.5, 0.06, HoldColorTestPhoto.Green));
        using var pixels = HoldPhotoPixels.TryOpen(photo)!;

        var left = HoldColorSampler.Estimate(new Hold { X = 0.25, Y = 0.5, Radius = 0.07 }, pixels.Width, pixels.Height, pixels.At);
        var right = HoldColorSampler.Estimate(new Hold { X = 0.75, Y = 0.5, Radius = 0.07 }, pixels.Width, pixels.Height, pixels.At);
        var wall = HoldColorSampler.Estimate(new Hold { X = 0.5, Y = 0.2, Radius = 0.05 }, pixels.Width, pixels.Height, pixels.At);

        Assert.Equal(("red", "green"), (left.Color, right.Color));
        Assert.Equal((null, HoldColorVerdict.LikeSurroundings), (wall.Color, wall.Verdict));
        Assert.Null(HoldPhotoPixels.TryOpen([1, 2, 3]));
        Assert.Null(HoldPhotoPixels.TryOpen(null));
    }

    private static double Distance(int x, int y, double cx, double cy) => Math.Sqrt(((x - cx) * (x - cx)) + ((y - cy) * (y - cy)));

    // An axis-aligned box around the hold centre, as offsets in fractions of the photo's width and height.
    private static List<ShapePoint> Ring(double halfWidth, double halfHeight) =>
    [
        new() { Dx = -halfWidth, Dy = -halfHeight },
        new() { Dx = halfWidth, Dy = -halfHeight },
        new() { Dx = halfWidth, Dy = halfHeight },
        new() { Dx = -halfWidth, Dy = halfHeight },
    ];
}
