namespace Blocwerk.HoldDetection.Matching;

/// <summary>A 2D point in image pixels (double precision, matching the reference pipeline).</summary>
/// <param name="X">X coordinate in pixels.</param>
/// <param name="Y">Y coordinate in pixels.</param>
public readonly record struct Pt(double X, double Y)
{
    /// <summary>Squared Euclidean distance to another point.</summary>
    public double Dist2(Pt o)
    {
        double dx = X - o.X;
        double dy = Y - o.Y;
        return (dx * dx) + (dy * dy);
    }

    /// <summary>Euclidean distance to another point.</summary>
    public double Dist(Pt o)
    {
        return Math.Sqrt(Dist2(o));
    }
}

/// <summary>
/// Small geometry primitives shared by the overlap matcher: brute-force k-nearest-neighbour
/// (band sizes are only a few hundred points) and a tiny weighted least-squares affine solve.
/// </summary>
internal static class MatchGeometry
{
    /// <summary>
    /// Returns the indices of the <paramref name="k"/> nearest points in <paramref name="pool"/>
    /// to <paramref name="query"/>, ascending by distance, together with their distances.
    /// </summary>
    public static (int[] Idx, double[] Dist) KNearest(IReadOnlyList<Pt> pool, Pt query, int k)
    {
        int n = pool.Count;
        k = Math.Min(k, n);
        var d2 = new double[n];
        var order = new int[n];
        for (int i = 0; i < n; i++)
        {
            d2[i] = pool[i].Dist2(query);
            order[i] = i;
        }

        Array.Sort(d2, order);
        var idx = new int[k];
        var dist = new double[k];
        for (int i = 0; i < k; i++)
        {
            idx[i] = order[i];
            dist[i] = Math.Sqrt(d2[i]);
        }

        return (idx, dist);
    }

    /// <summary>
    /// Solves a weighted affine map from anchor pairs: finds M (3x2) minimising
    /// Σ w·‖[ax,ay,1]·M − [bx,by]‖². Returns the mapped position of <paramref name="query"/>.
    /// A small ridge term keeps the 3x3 normal system non-singular for collinear anchors.
    /// </summary>
    public static Pt WeightedAffinePredict(
        IReadOnlyList<Pt> src,
        IReadOnlyList<Pt> dst,
        IReadOnlyList<double> weights,
        Pt query)
    {
        // Normal equations (XᵀWX) M = XᵀW Y, with X rows = [x, y, 1].
        // Accumulate the symmetric 3x3 XᵀWX and the two 3-vectors XᵀW·bx, XᵀW·by.
        double s00 = 0, s01 = 0, s02 = 0, s11 = 0, s12 = 0, s22 = 0;
        double bx0 = 0, bx1 = 0, bx2 = 0;
        double by0 = 0, by1 = 0, by2 = 0;
        for (int i = 0; i < src.Count; i++)
        {
            double w = weights[i];
            double x = src[i].X;
            double y = src[i].Y;
            double tx = dst[i].X;
            double ty = dst[i].Y;
            s00 += w * x * x;
            s01 += w * x * y;
            s02 += w * x;
            s11 += w * y * y;
            s12 += w * y;
            s22 += w;
            bx0 += w * x * tx;
            bx1 += w * y * tx;
            bx2 += w * tx;
            by0 += w * x * ty;
            by1 += w * y * ty;
            by2 += w * ty;
        }

        const double ridge = 1e-6;
        s00 += ridge;
        s11 += ridge;
        s22 += ridge;

        double[,] a =
        {
            { s00, s01, s02 },
            { s01, s11, s12 },
            { s02, s12, s22 },
        };

        var cx = Solve3(a, bx0, bx1, bx2);
        var cy = Solve3(a, by0, by1, by2);
        double px = (cx[0] * query.X) + (cx[1] * query.Y) + cx[2];
        double py = (cy[0] * query.X) + (cy[1] * query.Y) + cy[2];
        return new Pt(px, py);
    }

    /// <summary>Solves a 3x3 linear system A·x = b by Gaussian elimination with partial pivoting.</summary>
    private static double[] Solve3(double[,] a, double b0, double b1, double b2)
    {
        double[,] m =
        {
            { a[0, 0], a[0, 1], a[0, 2], b0 },
            { a[1, 0], a[1, 1], a[1, 2], b1 },
            { a[2, 0], a[2, 1], a[2, 2], b2 },
        };

        for (int col = 0; col < 3; col++)
        {
            int piv = col;
            for (int r = col + 1; r < 3; r++)
            {
                if (Math.Abs(m[r, col]) > Math.Abs(m[piv, col]))
                {
                    piv = r;
                }
            }

            if (piv != col)
            {
                for (int c = 0; c < 4; c++)
                {
                    (m[col, c], m[piv, c]) = (m[piv, c], m[col, c]);
                }
            }

            double d = m[col, col];
            if (Math.Abs(d) < 1e-12)
            {
                d = d < 0 ? -1e-12 : 1e-12;
            }

            for (int r = 0; r < 3; r++)
            {
                if (r == col)
                {
                    continue;
                }

                double f = m[r, col] / d;
                for (int c = col; c < 4; c++)
                {
                    m[r, c] -= f * m[col, c];
                }
            }
        }

        return new[] { m[0, 3] / m[0, 0], m[1, 3] / m[1, 1], m[2, 3] / m[2, 2] };
    }
}
