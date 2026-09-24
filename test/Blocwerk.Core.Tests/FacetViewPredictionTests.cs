using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.TextureRegistration;
using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <see cref="FacetViewPrediction"/> on a synthetic pinhole camera (f = 3000 px, 4000 × 3000 px, principal point at
/// the centre) looking obliquely at a wall facet "0" in the plane y = 0: from facet 0's exact registration alone it
/// must recover the focal length and pose and predict where a second, folded facet shows in the photo.
/// </summary>
public class FacetViewPredictionTests
{
    private const int Width = 4000;
    private const int Height = 3000;
    private const double Focal = 3000;

    private static readonly double[] Centre = [1500, -3000, 1200];
    private static readonly double[] LookAt = [2500, 0, 1500];
    private static readonly FacetFrame Anchor = Frame([0, 0, 0], [1, 0, 0]);
    private static readonly TexturePlaneFrame Texture = new("5", -100, 1100, -100, 2100, 1200, 2200);

    [Fact]
    public void FocalLength_IsRecoveredFromAnObliqueView()
    {
        var g = PlaneToPhoto(Anchor).Coefficients;

        var f = FacetViewPrediction.SelfCalibratedFocal(g, Width / 2.0, Height / 2.0, Width);

        Assert.NotNull(f);
        Assert.Equal(Focal, f!.Value, 1);
    }

    [Fact]
    public void FoldedFacet_IsPredictedWhereThePhotoShowsIt()
    {
        var target = Frame([4000, 0, 0], [0.7071068, -0.7071068, 0]);

        var seed = FacetViewPrediction.PhotoToTexture(Registration(), Anchor, target, Texture, Width, Height, null);

        Assert.NotNull(seed);
        var predicted = PlaneHomography.FromCoefficients(seed!);
        var toPhoto = PlaneToPhoto(target);
        var checkedPoints = 0;
        for (var a = 0.0; a <= 1000; a += 250)
        {
            for (var b = 0.0; b <= 2000; b += 500)
            {
                var (x, y) = toPhoto.Apply(a, b);
                if (x < 0 || x >= Width || y < 0 || y >= Height)
                {
                    continue;
                }

                var (u, v) = predicted.Apply(x, y);
                var (eu, ev) = Texture.ToPixel(a, b);
                Assert.Equal(eu, u, 0.5);
                Assert.Equal(ev, v, 0.5);
                checkedPoints++;
            }
        }

        Assert.True(checkedPoints >= 5);
    }

    [Fact]
    public void FacetFacingAwayFromTheCamera_IsNotPredicted()
    {
        var away = Frame([4000, 0, 0], [-0.7071068, 0.7071068, 0]);

        Assert.Null(FacetViewPrediction.PhotoToTexture(Registration(), Anchor, away, Texture, Width, Height, null));
    }

    /// <summary>Facet 0's exact registration: normalised photo → plane.</summary>
    private static FacetRegistration Registration()
    {
        var toPlane = PlaneHomography.FromCoefficients([Width, 0, 0, 0, Height, 0, 0, 0, 1]).Then(PlaneToPhoto(Anchor).Inverse()!);
        var extent = new PlaneRectMm(0, 5000, 0, 3000);
        return new FacetRegistration("0", true, 500, 500, 0, 0.6, 0.5, 1, null, toPlane, extent, Math.Sign(toPlane.Depth(0.5, 0.5)));
    }

    private static FacetFrame Frame(double[] origin, double[] u) =>
        FacetFrame.From(new WallGeometryFacet { Id = "x", Origin = origin, U = u, V = [0, 0, 1] })!;

    /// <summary>The true plane → photo px homography of a facet: K·[R·u | R·v | R·o + t].</summary>
    private static PlaneHomography PlaneToPhoto(FacetFrame facet)
    {
        var forward = Normalize(Sub(LookAt, Centre));
        var right = Normalize(Cross(forward, [0, 0, 1]));
        var down = Cross(forward, right);
        double[][] r = [right, down, forward];
        var o = Sub(facet.Origin, Centre);
        var g = new double[9];
        for (var row = 0; row < 3; row++)
        {
            double[] m = [Dot(r[row], facet.U), Dot(r[row], facet.V), Dot(r[row], o)];
            for (var c = 0; c < 3; c++)
            {
                g[(3 * row) + c] = row == 2 ? m[c] : Focal * m[c];
            }
        }

        for (var c = 0; c < 3; c++)
        {
            g[c] += Width / 2.0 * g[6 + c];
            g[3 + c] += Height / 2.0 * g[6 + c];
        }

        return PlaneHomography.FromCoefficients(g);
    }

    private static double[] Sub(double[] a, double[] b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];

    private static double Dot(double[] a, double[] b) => (a[0] * b[0]) + (a[1] * b[1]) + (a[2] * b[2]);

    private static double[] Normalize(double[] a)
    {
        var n = Math.Sqrt(Dot(a, a));
        return [a[0] / n, a[1] / n, a[2] / n];
    }

    private static double[] Cross(double[] a, double[] b) =>
        [(a[1] * b[2]) - (a[2] * b[1]), (a[2] * b[0]) - (a[0] * b[2]), (a[0] * b[1]) - (a[1] * b[0])];
}
