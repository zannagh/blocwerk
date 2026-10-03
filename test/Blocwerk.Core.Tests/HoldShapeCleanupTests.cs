using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Tests;

/// <summary>The entity-level clean-up: only auto outlines change, manual holds win, the plan is stable.</summary>
public sealed class HoldShapeCleanupTests
{
    [Fact]
    public void ManualShapesAndManualHoldsAreNeverChanged()
    {
        var manual = Hold(1, 0.30, shape: HoldShapeSmootherTests.Blob(0.05, 0.05, 24), source: HoldOutlineSource.Manual, auto: true);
        var placed = Hold(2, 0.60, shape: HoldShapeSmootherTests.Blob(0.05, 0.05, 24), source: null, auto: false);
        var auto = Hold(3, 0.38, shape: HoldShapeSmootherTests.Blob(0.05, 0.05, 24));

        var changes = HoldShapeCleanup.Plan([manual, placed, auto]);

        var change = Assert.Single(changes);
        Assert.Equal(auto.Id, change.HoldId);
        Assert.False(HoldShapeCleanup.IsCleanable(manual));
        Assert.False(HoldShapeCleanup.IsCleanable(placed));
    }

    [Fact]
    public void ASpikyAutoShapeIsSmoothed()
    {
        var shape = HoldShapeSmootherTests.Blob(0.03, 0.03, 24).ToList();
        shape.Insert(4, new ShapePoint { Dx = 0.09, Dy = 0.0 });
        var hold = Hold(1, 0.5, shape: shape);

        var change = Assert.Single(HoldShapeCleanup.Plan([hold]));

        Assert.NotNull(change.Shape);
        Assert.Equal(HoldShapeChangeKind.Smoothed, change.Kind);
        Assert.True(HoldShapeSmoother.IsSmooth(change.Shape));
    }

    [Fact]
    public void ARunOnItsOwnOutputChangesNothing()
    {
        var shape = HoldShapeSmootherTests.Blob(0.03, 0.03, 24).ToList();
        shape.Insert(4, new ShapePoint { Dx = 0.09, Dy = 0.0 });
        var a = Hold(1, 0.30, shape: shape);
        var b = Hold(2, 0.36, shape: HoldShapeSmootherTests.Blob(0.05, 0.05, 24));
        var holds = new[] { a, b };

        foreach (var change in HoldShapeCleanup.Plan(holds))
        {
            var hold = holds.Single(h => h.Id == change.HoldId);
            hold.ShapePoints = change.Shape;
            hold.Radius = change.Radius;
        }

        Assert.Empty(HoldShapeCleanup.Plan(holds));
    }

    [Fact]
    public void AnAutoShapeOverlappingAManualHoldYieldsToIt()
    {
        var manual = Hold(1, 0.30, shape: null, source: HoldOutlineSource.Manual, auto: false, radius: 0.05);
        var auto = Hold(2, 0.37, shape: HoldShapeSmootherTests.Blob(0.05, 0.05, 24));

        var change = Assert.Single(HoldShapeCleanup.Plan([manual, auto]));

        Assert.Equal(auto.Id, change.HoldId);
        Assert.NotEqual(HoldShapeChangeKind.Smoothed, change.Kind);
    }

    [Fact]
    public void FreshOutlinesAreResolvedAgainstEveryHoldOnThePanel()
    {
        var manual = Hold(1, 0.30, shape: HoldShapeSmootherTests.Blob(0.05, 0.05, 24), source: HoldOutlineSource.Manual, auto: false);
        var fresh = Hold(2, 0.37, shape: null);
        var outlines = new Dictionary<Blocwerk.Core.Entities.Hold, HoldOutlineResult> { [fresh] = Contour(fresh, HoldShapeSmootherTests.Blob(0.05, 0.05, 24)) };

        var resolved = HoldShapeCleanup.ResolveOutlines([manual, fresh], outlines, allowRadiusShrink: false);

        var (outline, _) = resolved[fresh];
        Assert.True(outline.ShapePoints is null || outline.ShapePoints.Min(p => p.Dx) > -0.04, "its left side was pulled back from the manual hold");
    }

    private static HoldOutlineResult Contour(Blocwerk.Core.Entities.Hold hold, List<ShapePoint> shape) =>
        new(
            HoldOutlineGeometry.ToPolygon(shape, hold.X, hold.Y).ToList(),
            hold.X,
            hold.Y,
            shape,
            100,
            new HoldOutlineBounds(0, 0, 0, 0),
            0.8,
            HoldOutlineMethod.Contour,
            new HoldFingerprint());

    private static Blocwerk.Core.Entities.Hold Hold(
        int n,
        double x,
        List<ShapePoint>? shape,
        HoldOutlineSource? source = HoldOutlineSource.AutoContour,
        bool auto = true,
        double radius = 0.05) =>
        new()
        {
            Id = new Guid(n, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]),
            X = x,
            Y = 0.5,
            Radius = radius,
            ShapePoints = shape,
            OutlineSource = source,
            IsAutoDetected = auto,
        };
}
