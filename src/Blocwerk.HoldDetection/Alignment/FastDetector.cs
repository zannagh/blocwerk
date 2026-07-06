namespace Blocwerk.HoldDetection.Alignment;

internal readonly record struct Keypoint(int X, int Y, double Angle, int Score);

/// <summary>
/// FAST-9 corner detector with intensity-centroid orientation, used to seed the
/// oriented-BRIEF descriptors for image alignment.
/// </summary>
internal static class FastDetector
{
    // Bresenham circle of radius 3 (16 pixels), clockwise from top.
    private static readonly (int Dx, int Dy)[] Ring =
    [
        (0, -3), (1, -3), (2, -2), (3, -1), (3, 0), (3, 1), (2, 2), (1, 3),
        (0, 3), (-1, 3), (-2, 2), (-3, 1), (-3, 0), (-3, -1), (-2, -2), (-1, -3),
    ];

    private const int Border = 18;
    private const int OrientationRadius = 15;

    public static List<Keypoint> Detect(GrayImage img, int threshold = 20, int maxKeypoints = 1500)
    {
        var w = img.Width;
        var h = img.Height;
        var px = img.Pixels;
        var scores = new int[w * h];
        var candidates = new List<(int X, int Y, int Score)>();

        for (var y = Border; y < h - Border; y++)
        {
            for (var x = Border; x < w - Border; x++)
            {
                var p = px[(y * w) + x];
                var score = CornerScore(px, w, x, y, p, threshold);
                if (score > 0)
                {
                    scores[(y * w) + x] = score;
                    candidates.Add((x, y, score));
                }
            }
        }

        // Non-max suppression in a 3x3 neighborhood.
        var kept = new List<(int X, int Y, int Score)>(candidates.Count);
        foreach (var (x, y, score) in candidates)
        {
            var isMax = true;
            for (var dy = -1; dy <= 1 && isMax; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0)
                    {
                        continue;
                    }

                    if (scores[((y + dy) * w) + x + dx] > score)
                    {
                        isMax = false;
                        break;
                    }
                }
            }

            if (isMax)
            {
                kept.Add((x, y, score));
            }
        }

        if (kept.Count > maxKeypoints)
        {
            kept.Sort((a, b) => b.Score.CompareTo(a.Score));
            kept.RemoveRange(maxKeypoints, kept.Count - maxKeypoints);
        }

        var result = new List<Keypoint>(kept.Count);
        foreach (var (x, y, score) in kept)
        {
            result.Add(new Keypoint(x, y, Orientation(px, w, x, y), score));
        }

        return result;
    }

    private static int CornerScore(byte[] px, int w, int x, int y, byte p, int t)
    {
        var hi = p + t;
        var lo = p - t;

        // Fast reject using the 4 compass pixels (indices 0,4,8,12).
        var brighter = 0;
        var darker = 0;
        for (var k = 0; k < 16; k += 4)
        {
            var v = px[((y + Ring[k].Dy) * w) + x + Ring[k].Dx];
            if (v > hi)
            {
                brighter++;
            }
            else if (v < lo)
            {
                darker++;
            }
        }

        if (brighter < 3 && darker < 3)
        {
            return 0;
        }

        // Check for 9 contiguous ring pixels all brighter or all darker.
        if (!HasArc(px, w, x, y, hi, lo, out var sad))
        {
            return 0;
        }

        return sad;
    }

    private static bool HasArc(byte[] px, int w, int x, int y, int hi, int lo, out int sad)
    {
        Span<int> vals = stackalloc int[16];
        sad = 0;
        for (var k = 0; k < 16; k++)
        {
            var v = px[((y + Ring[k].Dy) * w) + x + Ring[k].Dx];
            vals[k] = v;
        }

        var runBright = 0;
        var runDark = 0;
        // Iterate 16 + 8 to allow the arc to wrap around the ring.
        for (var k = 0; k < 24; k++)
        {
            var v = vals[k % 16];
            if (v > hi)
            {
                runBright++;
                runDark = 0;
            }
            else if (v < lo)
            {
                runDark++;
                runBright = 0;
            }
            else
            {
                runBright = 0;
                runDark = 0;
            }

            if (runBright >= 9 || runDark >= 9)
            {
                var mid = (hi + lo) / 2;
                for (var i = 0; i < 16; i++)
                {
                    sad += Math.Abs(vals[i] - mid);
                }

                return true;
            }
        }

        return false;
    }

    private static double Orientation(byte[] px, int w, int cx, int cy)
    {
        long m01 = 0;
        long m10 = 0;
        var r2 = OrientationRadius * OrientationRadius;
        for (var dy = -OrientationRadius; dy <= OrientationRadius; dy++)
        {
            for (var dx = -OrientationRadius; dx <= OrientationRadius; dx++)
            {
                if ((dx * dx) + (dy * dy) > r2)
                {
                    continue;
                }

                var v = px[((cy + dy) * w) + cx + dx];
                m10 += (long)dx * v;
                m01 += (long)dy * v;
            }
        }

        return Math.Atan2(m01, m10);
    }
}
