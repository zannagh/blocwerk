using System.Numerics;
using OpenCvSharp;

namespace Blocwerk.HoldDetection.Matching;

/// <summary>
/// Descriptor matching restricted to a neighbourhood: two images already brought into (roughly) the same
/// frame, so a feature's true partner lies within <c>radius</c> pixels. The ratio test then only competes
/// against local candidates — on a wall whose bolt-hole grid repeats every 20 cm a global ratio test throws
/// away almost every true match, because the second-best candidate is the next identical hole.
/// </summary>
internal static class GuidedDescriptorMatcher
{
    /// <summary>Largest Hamming distance (of AKAZE's 486 bits) still called a match.</summary>
    internal const int MaxHamming = 110;

    /// <summary>Lowe ratio among the local candidates.</summary>
    internal const double Ratio = 0.85;

    /// <summary>Matches every query keypoint to its best local train keypoint.</summary>
    /// <param name="query">Query keypoints.</param>
    /// <param name="queryDescriptors">Their binary descriptors (CV_8U, one row each).</param>
    /// <param name="train">Train keypoints, in the same frame.</param>
    /// <param name="trainDescriptors">Their descriptors.</param>
    /// <param name="radius">Search radius in pixels.</param>
    /// <returns>(query index, train index) pairs.</returns>
    public static List<(int Query, int Train)> Match(
        KeyPoint[] query, Mat queryDescriptors, KeyPoint[] train, Mat trainDescriptors, double radius)
    {
        var result = new List<(int Query, int Train)>();
        if (query.Length == 0 || train.Length == 0 || queryDescriptors.Cols != trainDescriptors.Cols)
        {
            return result;
        }

        var width = queryDescriptors.Cols;
        queryDescriptors.GetArray(out byte[] qd);
        trainDescriptors.GetArray(out byte[] td);
        var grid = Bucket(train, radius);
        for (var q = 0; q < query.Length; q++)
        {
            var best = Best(query[q].Pt, qd.AsSpan(q * width, width), train, td, width, grid, radius);
            if (best is { } t)
            {
                result.Add((q, t));
            }
        }

        return result;
    }

    private static int? Best(
        Point2f p, ReadOnlySpan<byte> descriptor, KeyPoint[] train, byte[] td, int width, Dictionary<(int X, int Y), List<int>> grid, double radius)
    {
        int best = -1, bestDist = int.MaxValue, second = int.MaxValue;
        var (cx, cy) = Cell(p, radius);
        var r2 = radius * radius;
        for (var dx = -1; dx <= 1; dx++)
        {
            for (var dy = -1; dy <= 1; dy++)
            {
                if (!grid.TryGetValue((cx + dx, cy + dy), out var cell))
                {
                    continue;
                }

                foreach (var t in cell)
                {
                    var d = train[t].Pt - p;
                    if ((d.X * d.X) + (d.Y * d.Y) > r2)
                    {
                        continue;
                    }

                    var dist = Hamming(descriptor, td.AsSpan(t * width, width));
                    if (dist < bestDist)
                    {
                        (second, bestDist, best) = (bestDist, dist, t);
                    }
                    else if (dist < second)
                    {
                        second = dist;
                    }
                }
            }
        }

        var distinct = second == int.MaxValue || bestDist < Ratio * second;
        return best >= 0 && bestDist <= MaxHamming && distinct ? best : null;
    }

    private static Dictionary<(int X, int Y), List<int>> Bucket(KeyPoint[] points, double size)
    {
        var grid = new Dictionary<(int X, int Y), List<int>>();
        for (var i = 0; i < points.Length; i++)
        {
            var key = Cell(points[i].Pt, size);
            if (!grid.TryGetValue(key, out var list))
            {
                grid[key] = list = [];
            }

            list.Add(i);
        }

        return grid;
    }

    private static (int X, int Y) Cell(Point2f p, double size) => ((int)Math.Floor(p.X / size), (int)Math.Floor(p.Y / size));

    private static int Hamming(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var sum = 0;
        for (var i = 0; i < a.Length; i++)
        {
            sum += BitOperations.PopCount((uint)(a[i] ^ b[i]));
        }

        return sum;
    }
}
