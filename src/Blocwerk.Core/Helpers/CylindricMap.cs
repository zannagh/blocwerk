namespace Blocwerk.Core.Helpers;

/// <summary>
/// The cylinder morph shared by the wall viewer's image warp and its hold overlay. A very wide
/// (fronto-parallel / ortho) wall is curved back toward the viewer so its far ends read less
/// stretched, panorama-style.
/// </summary>
/// <remarks>
/// <para>
/// Everything goes through one <see cref="MapPoint"/> in normalised [0,1]² space, pre-zoom, so it
/// composes with the CSS <c>--zoom</c> for free. The image is drawn as vertical strips and the SVG
/// overlay is mapped point-by-point through this SAME map, which is what keeps holds glued to the
/// photo. The JavaScript twin lives in <c>wwwroot/js/wall-view.js</c> (<c>bwCylindric</c>) and MUST
/// stay numerically identical.
/// </para>
/// <para>
/// Horizontal: <c>theta(u) = (u-0.5)·2·thetaMax</c>, <c>Xs(t) = sin(t)/(1 - k·cos(t))</c>,
/// normalised by <c>Xs(thetaMax)</c> so the edges pin to 0 and 1. Vertical: a per-column affine
/// scale <c>sc(t) = (1-k)/(1 - k·cos(t))</c> (1 at centre, &lt;1 at the edges — the transparent
/// wedges top and bottom). <c>beta ∈ [0,1]</c> lerps the whole thing to identity, so
/// <c>beta = 0</c> is exactly flat.
/// </para>
/// <para>
/// Monotonicity (a well-formed, non-folding warp) requires <c>k &lt; cos(thetaMax)</c>. The
/// defaults satisfy it: <c>cos(0.8) ≈ 0.697 &gt; 0.45</c>.
/// </para>
/// </remarks>
public readonly struct CylindricMap
{
    /// <summary>Default half-angle of the modelled cylinder, in radians (~46°).</summary>
    public const double DefaultThetaMax = 0.8;

    /// <summary>Default viewer-distance term. Must stay below <c>cos(thetaMax)</c>.</summary>
    public const double DefaultK = 0.45;

    private readonly double thetaMax;
    private readonly double k;
    private readonly double beta;
    private readonly double xsThetaMax;

    /// <summary>
    /// Initializes a new instance of the <see cref="CylindricMap"/> struct. <paramref name="beta"/>
    /// is the curve strength (0 = flat, 1 = full). <paramref name="thetaMax"/> is not stored on the
    /// wall yet, so callers use the default; the parameter is here so a real angular width from the
    /// pipeline can be threaded through later.
    /// </summary>
    public CylindricMap(double beta, double thetaMax = DefaultThetaMax, double k = DefaultK)
    {
        this.thetaMax = thetaMax;
        this.k = k;
        this.beta = Math.Clamp(beta, 0.0, 1.0);
        xsThetaMax = Xs(thetaMax, k);
    }

    /// <summary>Builds a map from the 0..100 curvature slider value.</summary>
    public static CylindricMap FromCurvature(int curvature, double thetaMax = DefaultThetaMax, double k = DefaultK)
    {
        return new CylindricMap(Math.Clamp(curvature, 0, 100) / 100.0, thetaMax, k);
    }

    /// <summary>Maps a normalised point to its warped normalised position.</summary>
    public (double X, double Y) MapPoint(double u, double v)
    {
        var theta = (u - 0.5) * 2.0 * thetaMax;
        var xn = Xs(theta, k) / xsThetaMax;
        var sxFull = 0.5 + 0.5 * xn;
        var sc = (1.0 - k) / (1.0 - k * Math.Cos(theta));
        var syFull = 0.5 + (v - 0.5) * sc;
        var sx = (1.0 - beta) * u + beta * sxFull;
        var sy = (1.0 - beta) * v + beta * syFull;
        return (sx, sy);
    }

    /// <summary>
    /// Tessellates a circle into a warped polygon. A mapped circle is not a circle, so callers draw
    /// the returned points as a <c>&lt;polygon&gt;</c>.
    /// </summary>
    public IEnumerable<(double X, double Y)> MapCircle(double cx, double cy, double r, int segments = 20)
    {
        var n = Math.Max(3, segments);
        for (var i = 0; i < n; i++)
        {
            var a = 2.0 * Math.PI * i / n;
            yield return MapPoint(cx + r * Math.Cos(a), cy + r * Math.Sin(a));
        }
    }

    private static double Xs(double t, double k)
    {
        return Math.Sin(t) / (1.0 - k * Math.Cos(t));
    }
}
