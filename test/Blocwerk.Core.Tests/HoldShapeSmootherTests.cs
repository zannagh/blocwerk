using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Tests;

/// <summary>Spikes, deep incuts and ragged edges become smooth outlines or are rejected (pure geometry).</summary>
public sealed class HoldShapeSmootherTests
{
    [Fact]
    public void ASmoothBlobIsReturnedAsIs()
    {
        var blob = Blob(0.03, 0.025, 24);

        Assert.True(HoldShapeSmoother.IsSmooth(blob));
        var result = HoldShapeSmoother.Smooth(blob);

        Assert.NotNull(result);
        Assert.Equal(blob.Count, result.Count);
    }

    [Fact]
    public void SmoothingIsIdempotent()
    {
        var rough = WithSpikes(Blob(0.03, 0.03, 32), spikeEvery: 8, length: 0.05);

        var once = HoldShapeSmoother.Smooth(rough);
        Assert.NotNull(once);
        var twice = HoldShapeSmoother.Smooth(once);

        Assert.NotNull(twice);
        Assert.Equal(once.Select(p => (p.Dx, p.Dy)), twice.Select(p => (p.Dx, p.Dy)));
    }

    [Fact]
    public void SpikesAreRemovedAndTheResultIsSmooth()
    {
        var spiky = WithSpikes(Blob(0.03, 0.03, 32), spikeEvery: 8, length: 0.05);
        Assert.False(HoldShapeSmoother.IsSmooth(spiky));

        var result = HoldShapeSmoother.Smooth(spiky);

        Assert.NotNull(result);
        Assert.True(HoldShapeSmoother.IsSmooth(result));
        Assert.InRange(result.Count, 3, HoldShapeSmoother.MaxVertices);
        Assert.True(result.Max(p => Math.Sqrt((p.Dx * p.Dx) + (p.Dy * p.Dy))) < 0.04, "the spikes (0.08 out) are gone");
    }

    [Fact]
    public void ADeepIncutIsBlendedTowardTheHull()
    {
        var notched = Blob(0.03, 0.03, 32).ToList();
        for (int i = 0; i < 3; i++)
        {
            var p = notched[i];
            notched[i] = new ShapePoint { Dx = p.Dx * 0.2, Dy = p.Dy * 0.2 };
        }

        var result = HoldShapeSmoother.Smooth(notched);

        Assert.NotNull(result);
        Assert.True(HoldShapeSmoother.IsSmooth(result));
        Assert.True(result.Min(p => Math.Sqrt((p.Dx * p.Dx) + (p.Dy * p.Dy))) > 0.02, "the notch no longer reaches the centre");
    }

    [Fact]
    public void AZigZagEdgeIsEitherSmoothedOrRejectedNeverKeptJagged()
    {
        var zigzag = Blob(0.03, 0.03, 40)
            .Select((p, i) => i % 2 == 0 ? p : new ShapePoint { Dx = p.Dx * 0.3, Dy = p.Dy * 0.3 })
            .ToList();

        var result = HoldShapeSmoother.Smooth(zigzag);

        Assert.True(result is null || HoldShapeSmoother.IsSmooth(result));
    }

    [Fact]
    public void ASelfCrossingOutlineIsRejected()
    {
        var bowtie = new List<ShapePoint>
        {
            new() { Dx = -0.03, Dy = -0.03 },
            new() { Dx = 0.03, Dy = 0.03 },
            new() { Dx = 0.03, Dy = -0.03 },
            new() { Dx = -0.03, Dy = 0.03 },
        };

        Assert.Null(HoldShapeSmoother.Smooth(bowtie));
    }

    [Fact]
    public void ADegenerateOrCentrelessShapeIsRejected()
    {
        var sliver = new List<ShapePoint>
        {
            new() { Dx = -0.03, Dy = 0 },
            new() { Dx = 0.03, Dy = 0 },
            new() { Dx = 0.03, Dy = 0.0000001 },
        };
        var elsewhere = Blob(0.01, 0.01, 12).Select(p => new ShapePoint { Dx = p.Dx + 0.1, Dy = p.Dy }).ToList();

        Assert.Null(HoldShapeSmoother.Smooth(sliver));
        Assert.Null(HoldShapeSmoother.Smooth(elsewhere));
    }

    [Fact]
    public void ARectangleGetsRoundedCornersWithinTheVertexBudget()
    {
        var rect = new List<ShapePoint>
        {
            new() { Dx = -0.04, Dy = -0.02 },
            new() { Dx = 0.04, Dy = -0.02 },
            new() { Dx = 0.04, Dy = 0.02 },
            new() { Dx = -0.04, Dy = 0.02 },
        };

        var result = HoldShapeSmoother.Smooth(rect);

        Assert.NotNull(result);
        Assert.InRange(result.Count, 8, HoldShapeSmoother.MaxVertices);
        Assert.True(HoldShapeSmoother.IsSmooth(result));
    }

    internal static List<ShapePoint> Blob(double rx, double ry, int n) =>
        Enumerable.Range(0, n).Select(i =>
        {
            double a = 2 * Math.PI * i / n;
            return new ShapePoint { Dx = Math.Round(rx * Math.Cos(a), 5), Dy = Math.Round(ry * Math.Sin(a), 5) };
        }).ToList();

    private static List<ShapePoint> WithSpikes(List<ShapePoint> shape, int spikeEvery, double length)
    {
        var result = new List<ShapePoint>();
        for (int i = 0; i < shape.Count; i++)
        {
            result.Add(shape[i]);
            if (i % spikeEvery == 0)
            {
                double mag = Math.Sqrt((shape[i].Dx * shape[i].Dx) + (shape[i].Dy * shape[i].Dy));
                double scale = (mag + length) / mag;
                result.Add(new ShapePoint { Dx = shape[i].Dx * scale, Dy = shape[i].Dy * scale });
            }
        }

        return result;
    }
}
