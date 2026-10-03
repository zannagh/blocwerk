// <copyright file="OutlineConfidenceScoringTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;
using Blocwerk.HoldDetection.Outlines;
using OpenCvSharp;

namespace Blocwerk.HoldDetection.Tests.Outlines;

/// <summary>
/// The confidence the wall update's shape review sorts by (lowest first) is the outliner's own score. It has
/// to separate the cases a reviewer cares about: a clean colour blob scores high, a hold the segmenter could
/// not tell from the wall falls back to the circle and scores low, and less contrast never scores higher.
/// </summary>
public class OutlineConfidenceScoringTests
{
    [Fact]
    public void CleanColourBlob_ScoresHigh()
    {
        var result = OutlineOnWood(new Scalar(40, 40, 200));

        Assert.Equal(HoldOutlineMethod.Contour, result.Method);
        Assert.True(result.Confidence >= 0.7, $"confidence {result.Confidence}");
        Assert.True(Classify(result) == HoldOutlineUpgradeOutcome.Outline);
    }

    [Fact]
    public void WallColouredVolume_FallsBackToTheCircle_AndScoresLow()
    {
        // Within a few units of the synthetic wood (168, 192, 214): nothing to segment against.
        var result = OutlineOnWood(new Scalar(163, 188, 210));

        Assert.Equal(HoldOutlineMethod.CircleFallback, result.Method);
        Assert.Null(result.ShapePoints);
        Assert.True(result.Confidence <= 0.2, $"confidence {result.Confidence}");
        Assert.True(Classify(result) == HoldOutlineUpgradeOutcome.KeepCircle);
    }

    [Fact]
    public void PaleHoldOnWood_IsOutlinedByItsEdges_AndRankedForReview()
    {
        // Pale beige on the synthetic wood: too little colour contrast for the colour pass, a clear edge though.
        var result = OutlineOnWood(new Scalar(200, 212, 222));

        Assert.Equal(HoldOutlineMethod.GrabCut, result.Method);
        Assert.NotNull(result.ShapePoints);
        Assert.InRange(result.Confidence, 0.2, 0.7);
    }

    [Fact]
    public void LessContrast_NeverScoresHigher()
    {
        var strong = OutlineOnWood(new Scalar(40, 40, 200));
        var faint = OutlineOnWood(new Scalar(120, 150, 205));

        Assert.True(faint.Confidence <= strong.Confidence, $"faint {faint.Confidence} vs strong {strong.Confidence}");
    }

    private static HoldOutlineResult OutlineOnWood(Scalar colour)
    {
        using Mat img = SyntheticWall.Wood(600, 500);
        Point[] truth = SyntheticWall.Blob(new Point(300, 240), 80, 60, seed: 11);
        SyntheticWall.PaintHold(img, truth, colour, 0);
        Rect box = Cv2.BoundingRect(truth);
        HoldSeed seed = HoldSeed.FromPixelBox(box.X, box.Y, box.Width, box.Height, img.Width, img.Height);
        using var session = new OpenCvHoldOutlineService().OpenSession(img);
        return session.Outline(seed);
    }

    private static HoldOutlineUpgradeOutcome Classify(HoldOutlineResult result)
    {
        var hold = new Hold { X = result.AnchorX, Y = result.AnchorY, Radius = 0.1, IsAutoDetected = true };
        return HoldOutlineUpgradePlanner.Classify(hold, result, new HoldSeed(hold.X, hold.Y, hold.Radius), 600, 500);
    }
}
