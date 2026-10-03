using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Tests;

/// <summary>The per-outline post-processing every outliner result goes through: smooth, or circle fallback.</summary>
public sealed class HoldOutlineRefinerTests
{
    [Fact]
    public void ASmoothContourKeepsItsMethod()
    {
        var result = HoldOutlineRefiner.Refine(Contour(HoldShapeSmootherTests.Blob(0.03, 0.03, 24)), 1.33);

        Assert.Equal(HoldOutlineMethod.Contour, result.Method);
        Assert.True(HoldShapeSmoother.IsSmooth(result.ShapePoints!, 1.33));
    }

    [Fact]
    public void AnUnfixableContourBecomesACircleFallback()
    {
        var bowtie = new List<ShapePoint>
        {
            new() { Dx = -0.03, Dy = -0.03 },
            new() { Dx = 0.03, Dy = 0.03 },
            new() { Dx = 0.03, Dy = -0.03 },
            new() { Dx = -0.03, Dy = 0.03 },
        };

        var result = HoldOutlineRefiner.Refine(Contour(bowtie), 1);

        Assert.Equal(HoldOutlineMethod.CircleFallback, result.Method);
        Assert.Null(result.ShapePoints);
        Assert.True(result.Confidence <= HoldOutlineRefiner.CircleConfidenceCeiling);
    }

    private static HoldOutlineResult Contour(List<ShapePoint> shape) =>
        new(
            HoldOutlineGeometry.ToPolygon(shape, 0.5, 0.5).ToList(),
            0.5,
            0.5,
            shape,
            100,
            new HoldOutlineBounds(0, 0, 0, 0),
            0.8,
            HoldOutlineMethod.Contour,
            new HoldFingerprint());
}
