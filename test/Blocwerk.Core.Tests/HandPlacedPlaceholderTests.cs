// <copyright file="HandPlacedPlaceholderTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A small circle somebody placed on a hold the detector missed, and the wall update that finally detects the hold:
/// the detection is that hold (it keeps its identity and takes the shape), not a second hold beside the circle and
/// not a discarded detection.
/// </summary>
public class HandPlacedPlaceholderTests
{
    [Fact]
    public void ASmallCircleInsideAConfidentTracedDetection_IsMergedWithIt_ThoughTheSizesDiffer()
    {
        var circle = new Hold { X = 0.5, Y = 0.5, Radius = 0.012, IsAutoDetected = false, Name = "Jug" };
        var detection = Traced(0.52, 0.51, 0.04, confidence: 0.95);

        var result = HandPlacedMerger.Find([circle], [detection], null, null);

        Assert.Equal(new HandPlacedMerge(circle.Id, detection.Id), Assert.Single(result.Merges));
    }

    [Fact]
    public void ADetectionTheDetectorIsNotSureOf_OrWithoutATracedOutline_IsNotMergedWithACircleOfAnotherSize()
    {
        var circle = new Hold { X = 0.5, Y = 0.5, Radius = 0.012, IsAutoDetected = false };
        var unsure = Traced(0.52, 0.51, 0.04, confidence: 0.6);
        var untraced = new Hold { X = 0.52, Y = 0.51, Radius = 0.04, IsAutoDetected = true, Confidence = 0.95 };

        Assert.Empty(HandPlacedMerger.Find([circle], [unsure], null, null).Merges);
        Assert.Empty(HandPlacedMerger.Find([circle], [untraced], null, null).Merges);
    }

    [Fact]
    public void ACircleOutsideTheOutline_OrFarTooSmallForIt_IsNotMerged()
    {
        var beside = new Hold { X = 0.58, Y = 0.5, Radius = 0.012, IsAutoDetected = false };
        var speck = new Hold { X = 0.52, Y = 0.51, Radius = 0.005, IsAutoDetected = false };
        var detection = Traced(0.52, 0.51, 0.04, confidence: 0.95);

        Assert.Empty(HandPlacedMerger.Find([beside], [detection], null, null).Merges);
        Assert.Empty(HandPlacedMerger.Find([speck], [detection], null, null).Merges);
    }

    [Fact]
    public void TwoCirclesInsideOneDetection_AreLeftForAPerson()
    {
        var a = new Hold { X = 0.5, Y = 0.5, Radius = 0.012, IsAutoDetected = false };
        var b = new Hold { X = 0.54, Y = 0.52, Radius = 0.012, IsAutoDetected = false };

        var result = HandPlacedMerger.Find([a, b], [Traced(0.52, 0.51, 0.04, confidence: 0.95)], null, null);

        Assert.Empty(result.Merges);
        Assert.Equal(2, result.Ambiguous.Count);
    }

    private static Hold Traced(double x, double y, double half, double confidence) => new()
    {
        X = x,
        Y = y,
        Radius = half,
        IsAutoDetected = true,
        Confidence = confidence,
        OutlineConfidence = 0.9,
        ShapePoints = [new() { Dx = -half, Dy = -half }, new() { Dx = half, Dy = -half }, new() { Dx = half, Dy = half }, new() { Dx = -half, Dy = half }],
    };
}
