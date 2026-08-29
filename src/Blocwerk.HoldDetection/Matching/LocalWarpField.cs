namespace Blocwerk.HoldDetection.Matching;

/// <summary>
/// Locally-weighted-affine map from anchor correspondences. A single homography cannot
/// map one perspective photo of a non-planar wall (facets, kickboard, corner) onto
/// another — different depths shift by different amounts (parallax). So each point is
/// predicted from a weighted affine fit over its <c>k</c> nearest anchors, which absorbs
/// the non-planarity. Faithful port of the Python <c>WarpField</c>.
/// </summary>
public sealed class LocalWarpField
{
    private readonly IReadOnlyList<Pt> src;
    private readonly IReadOnlyList<Pt> dst;
    private readonly int k;

    /// <summary>Initializes a new instance of the <see cref="LocalWarpField"/> class from paired anchors (src → dst).</summary>
    /// <param name="src">Anchor source points.</param>
    /// <param name="dst">Anchor destination points (same length/order as <paramref name="src"/>).</param>
    /// <param name="k">Neighbourhood size for the local affine fit.</param>
    public LocalWarpField(IReadOnlyList<Pt> src, IReadOnlyList<Pt> dst, int k = 20)
    {
        this.src = src;
        this.dst = dst;
        this.k = Math.Min(k, src.Count);
    }

    /// <summary>Number of anchors backing this field.</summary>
    public int AnchorCount => src.Count;

    /// <summary>Predicts the destination position of a single source point.</summary>
    public Pt Predict(Pt p)
    {
        var (idx, dist) = MatchGeometry.KNearest(src, p, k);
        double median = Median(dist);
        double denom = median + 1e-6;

        var localSrc = new Pt[idx.Length];
        var localDst = new Pt[idx.Length];
        var w = new double[idx.Length];
        for (int i = 0; i < idx.Length; i++)
        {
            localSrc[i] = src[idx[i]];
            localDst[i] = dst[idx[i]];
            double r = dist[i] / denom;
            // The reference scales both least-squares matrices by exp(-(r²)), so the effective
            // quadratic weight (what the normal equations here take) is that value squared.
            double wi = Math.Exp(-(r * r));
            w[i] = wi * wi;
        }

        return MatchGeometry.WeightedAffinePredict(localSrc, localDst, w, p);
    }

    /// <summary>Predicts the destination positions of many source points.</summary>
    public Pt[] Predict(IReadOnlyList<Pt> pts)
    {
        var outPts = new Pt[pts.Count];
        for (int i = 0; i < pts.Count; i++)
        {
            outPts[i] = Predict(pts[i]);
        }

        return outPts;
    }

    private static double Median(double[] values)
    {
        var copy = (double[])values.Clone();
        Array.Sort(copy);
        int n = copy.Length;
        if (n == 0)
        {
            return 0.0;
        }

        return n % 2 == 1 ? copy[n / 2] : 0.5 * (copy[(n / 2) - 1] + copy[n / 2]);
    }
}
