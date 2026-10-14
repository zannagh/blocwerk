// <copyright file="SegmentWindowsTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.HoldDetection.Segmentation;

namespace Blocwerk.HoldDetection.Tests.Segmentation;

/// <summary>The windows the segmenter looks at: every hold in exactly one, at a scale that suits it, always the same.</summary>
public class SegmentWindowsTests
{
    [Fact]
    public void EveryBox_IsInExactlyOneWindow_ThatContainsIt()
    {
        var rng = new Random(11);
        var boxes = Enumerable.Range(0, 300).Select(_ => Box(rng.Next(0, 3900), rng.Next(0, 2900), rng.Next(12, 260), rng.Next(12, 260))).ToList();

        var windows = SegmentWindows.Plan(boxes, 4032, 3024);

        Assert.Equal(Enumerable.Range(0, boxes.Count), windows.SelectMany(w => w.Members).Order());
        Assert.All(windows, w => Assert.True(w.X >= 0 && w.Y >= 0 && w.X + w.Width <= 4032 && w.Y + w.Height <= 3024));
    }

    [Fact]
    public void NeighboursOfASimilarSize_ShareAWindow_AndAFootChipDoesNotJoinAVolume()
    {
        var volume = Box(1000, 1000, 600, 500);
        var jug = Box(2500, 800, 120, 100);
        var nextJug = Box(2700, 850, 110, 120);
        var chip = Box(1250, 1200, 24, 20);

        var windows = SegmentWindows.Plan([volume, jug, nextJug, chip], 4032, 3024);

        Assert.Contains(windows, w => w.Members.Contains(1) && w.Members.Contains(2));
        Assert.DoesNotContain(windows, w => w.Members.Contains(0) && w.Members.Contains(3));
    }

    [Fact]
    public void AWindow_IsSixLongSidesWide_AndStaysInsideTheImage()
    {
        var (x, y, w, h) = SegmentWindows.Around(Box(10, 20, 100, 60), 4032, 3024);

        Assert.Equal((0, 0, 600, 600), (x, y, w, h));
        Assert.Equal((SegmentWindows.MinSide, SegmentWindows.MinSide), SizeOf(SegmentWindows.Around(Box(500, 500, 10, 8), 4032, 3024)));
        Assert.Equal((4032, 3024), SizeOf(SegmentWindows.Around(Box(500, 500, 1500, 900), 4032, 3024)));
    }

    [Fact]
    public void TheSameBoxes_AlwaysGiveTheSameWindows()
    {
        var rng = new Random(5);
        var boxes = Enumerable.Range(0, 120).Select(_ => Box(rng.Next(0, 3900), rng.Next(0, 2900), 60, 60)).ToList();

        var first = SegmentWindows.Plan(boxes, 4032, 3024);
        var second = SegmentWindows.Plan(boxes, 4032, 3024);

        Assert.Equal(first.Select(w => (w.X, w.Y, string.Join(',', w.Members))), second.Select(w => (w.X, w.Y, string.Join(',', w.Members))));
    }

    private static SegmentBox Box(int left, int top, int width, int height) => new(left, top, left + width, top + height);

    private static (int Width, int Height) SizeOf((int X, int Y, int Width, int Height) window) => (window.Width, window.Height);
}
