using System.Diagnostics;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.TextureRegistration;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The smooth residual field on a facet's homography (<see cref="ResidualWarp"/>), on synthetic photos of a textured
/// 6 × 8 m plane (3000 × 4000 px, the texture at 1 mm/px): a barrel-distorted one, where the field must beat the plain
/// homography inside the matches' hull and vanish far outside it, and ones where it must not appear (an undistorted photo
/// and pure matching noise).
/// </summary>
public class ResidualWarpTests
{
    private const int Width = 3000;
    private const int Height = 4000;

    private static readonly TexturePlaneFrame Frame = new("0", 0, 6000, 0, 8000, 6000, 8000);
    private static readonly PlaneRectMm Extent = new(0, 6000, 0, 8000);

    [Fact]
    public void BarrelDistortion_FieldReducesTheReprojectionError_InsideTheHull()
    {
        var r = Register(Barrel(0.12, 0.05, 0.62, 0.05, 0.9, 38));

        Assert.True(r.Accepted, r.Reason);
        Assert.NotNull(r.Warp);
        var (homography, warped) = HeldOutError(r, 0.12, 0.08, 0.6, 0.08, 0.86, 23);
        Assert.True(warped < 0.5 * homography, $"homography {homography:F1} mm, with the field {warped:F1} mm");
    }

    [Fact]
    public void FarBeyondTheHull_TheHomographyAloneIsUsed()
    {
        var r = Register(Barrel(0.12, 0.05, 0.62, 0.05, 0.9, 38));
        Assert.NotNull(r.Warp);

        foreach (var (x, y) in new[] { (0.97, 0.5), (0.9, 0.2), (0.95, 0.9) })
        {
            var (a, b) = r.Map(x, y);
            var (ha, hb) = r.PhotoToPlane!.Apply(x, y);
            Assert.Equal((ha, hb), (a, b));
        }

        var corrections = from i in Enumerable.Range(0, 41) from j in Enumerable.Range(0, 41) select r.Warp!.Correction(i / 40.0, j / 40.0);
        Assert.All(corrections, c => Assert.True(Math.Sqrt((c.A * c.A) + (c.B * c.B)) <= ResidualWarp.MaxCorrectionMm));
    }

    [Fact]
    public void TheCorrection_FadesPastTheLastMatches()
    {
        var r = Register(Barrel(0.12, 0.05, 0.62, 0.05, 0.9, 38));
        Assert.NotNull(r.Warp);

        double Size(double x) => Math.Abs(r.Warp!.Correction(x, 0.5).A) + Math.Abs(r.Warp!.Correction(x, 0.5).B);

        Assert.True(Size(0.62) > Size(0.74), "the correction fades past the last matches");
        Assert.True(Size(0.74) >= Size(0.9));
    }

    [Fact]
    public void AnUndistortedPhoto_KeepsTheHomographyAlone()
    {
        var r = Register(Barrel(0, 0.05, 0.9, 0.05, 0.9, 38));

        Assert.True(r.Accepted, r.Reason);
        Assert.Null(r.Warp);
    }

    [Fact]
    public void ResidualsThatAreOnlyNoise_AreNotFitted()
    {
        var random = new Random(7);
        var samples = Enumerable.Range(0, 1500)
            .Select(_ => new WarpSample(random.NextDouble(), random.NextDouble(), (random.NextDouble() - 0.5) * 30, (random.NextDouble() - 0.5) * 30))
            .ToList();

        Assert.Null(ResidualWarp.Fit(samples));
    }

    [Fact]
    public void TheFit_IsDeterministic_AndBoundedInTime()
    {
        var samples = Barrel(0.12, 0.05, 0.9, 0.05, 0.95, 90).Select(ToSample).ToList();
        var watch = Stopwatch.StartNew();
        var first = ResidualWarp.Fit(samples);
        var elapsed = watch.Elapsed;
        var second = ResidualWarp.Fit(samples);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.Correction(0.3, 0.4), second.Correction(0.3, 0.4));
        Assert.True(elapsed < TimeSpan.FromSeconds(3), $"fitting {samples.Count} pairs took {elapsed.TotalMilliseconds:F0} ms");
    }

    /// <summary>The ground truth: a photo point (normalised) seen through barrel distortion <paramref name="k"/> onto the plane.</summary>
    private static (double A, double B) TruePlane(double u, double v, double k)
    {
        var (dx, dy) = (u - 0.5, v - 0.5);
        var (sx, sy) = (dx, dy);
        for (var i = 0; i < 20; i++)
        {
            var r2 = (sx * sx) + (sy * sy);
            (sx, sy) = (dx / (1 + (k * r2)), dy / (1 + (k * r2)));
        }

        return (6000 * (0.5 + sx), 8000 * (1 - (0.5 + sy)));
    }

    /// <summary>Matches on a grid of <paramref name="n"/> × <paramref name="n"/> photo points inside the given normalised box.</summary>
    private static List<PointCorrespondence> Barrel(double k, double x0, double x1, double y0, double y1, int n)
    {
        var pairs = new List<PointCorrespondence>();
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++)
            {
                var (u, v) = (x0 + ((x1 - x0) * i / (n - 1)), y0 + ((y1 - y0) * j / (n - 1)));
                var (a, b) = TruePlane(u, v, k);
                var (tx, ty) = Frame.ToPixel(a, b);
                pairs.Add(new PointCorrespondence(u * Width, v * Height, tx, ty));
            }
        }

        return pairs;
    }

    private static WarpSample ToSample(PointCorrespondence p)
    {
        var (u, v) = (p.SrcX / Width, p.SrcY / Height);
        var (ta, tb) = Frame.ToPlane(p.DstX, p.DstY);
        var (a, b) = TruePlane(u, v, 0);
        return new WarpSample(u, v, ta - a, tb - b);
    }

    private static FacetRegistration Register(List<PointCorrespondence> pairs) =>
        PhotoTextureRegistration.Register(new PhotoTextureMatch(pairs, 50, null), Width, Height, Frame, Extent);

    /// <summary>RMS distance to the true plane position over a grid of photo points, with the homography alone and with the field, mm.</summary>
    private static (double Homography, double Warped) HeldOutError(FacetRegistration r, double k, double x0, double x1, double y0, double y1, int n)
    {
        double homography = 0, warped = 0;
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++)
            {
                var (u, v) = (x0 + ((x1 - x0) * i / (n - 1)), y0 + ((y1 - y0) * j / (n - 1)));
                var (ta, tb) = TruePlane(u, v, k);
                var (ha, hb) = r.PhotoToPlane!.Apply(u, v);
                var (wa, wb) = r.Map(u, v);
                homography += Math.Pow(ha - ta, 2) + Math.Pow(hb - tb, 2);
                warped += Math.Pow(wa - ta, 2) + Math.Pow(wb - tb, 2);
            }
        }

        return (Math.Sqrt(homography / (n * n)), Math.Sqrt(warped / (n * n)));
    }
}
