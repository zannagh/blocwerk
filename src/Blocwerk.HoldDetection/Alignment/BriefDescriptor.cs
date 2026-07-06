namespace Blocwerk.HoldDetection.Alignment;

/// <summary>
/// 256-bit oriented BRIEF descriptor (4 x ulong per keypoint). The sampling
/// pattern is generated once with a fixed seed, so both images in a comparison
/// use the identical pattern.
/// </summary>
internal static class BriefDescriptor
{
    public const int WordsPerDescriptor = 4; // 256 bits

    private static readonly (sbyte Ax, sbyte Ay, sbyte Bx, sbyte By)[] Pattern = BuildPattern();

    /// <summary>Computes a flat descriptor array of length kps.Count * 4.</summary>
    public static ulong[] Compute(GrayImage blurred, IReadOnlyList<Keypoint> kps)
    {
        var descriptors = new ulong[kps.Count * WordsPerDescriptor];
        for (var i = 0; i < kps.Count; i++)
        {
            var kp = kps[i];
            var cos = Math.Cos(kp.Angle);
            var sin = Math.Sin(kp.Angle);
            var baseIdx = i * WordsPerDescriptor;

            for (var bit = 0; bit < 256; bit++)
            {
                var (ax, ay, bx, by) = Pattern[bit];
                var sa = Sample(blurred, kp.X, kp.Y, ax, ay, cos, sin);
                var sb = Sample(blurred, kp.X, kp.Y, bx, by, cos, sin);
                if (sa < sb)
                {
                    descriptors[baseIdx + (bit >> 6)] |= 1UL << (bit & 63);
                }
            }
        }

        return descriptors;
    }

    public static int Hamming(ulong[] a, int ai, ulong[] b, int bi)
    {
        var oa = ai * WordsPerDescriptor;
        var ob = bi * WordsPerDescriptor;
        var d = 0;
        for (var k = 0; k < WordsPerDescriptor; k++)
        {
            d += System.Numerics.BitOperations.PopCount(a[oa + k] ^ b[ob + k]);
        }

        return d;
    }

    private static byte Sample(GrayImage img, int cx, int cy, int px, int py, double cos, double sin)
    {
        var rx = (px * cos) - (py * sin);
        var ry = (px * sin) + (py * cos);
        var x = cx + rx;
        var y = cy + ry;

        // Bilinear with clamped coordinates so rotated samples never read OOB.
        if (x < 0)
        {
            x = 0;
        }
        else if (x > img.Width - 2)
        {
            x = img.Width - 2;
        }

        if (y < 0)
        {
            y = 0;
        }
        else if (y > img.Height - 2)
        {
            y = img.Height - 2;
        }

        var x0 = (int)x;
        var y0 = (int)y;
        var fx = x - x0;
        var fy = y - y0;
        var w = img.Width;
        var i00 = img.Pixels[(y0 * w) + x0];
        var i10 = img.Pixels[(y0 * w) + x0 + 1];
        var i01 = img.Pixels[((y0 + 1) * w) + x0];
        var i11 = img.Pixels[((y0 + 1) * w) + x0 + 1];
        var top = (i00 * (1 - fx)) + (i10 * fx);
        var bot = (i01 * (1 - fx)) + (i11 * fx);
        return (byte)((top * (1 - fy)) + (bot * fy));
    }

    private static (sbyte, sbyte, sbyte, sbyte)[] BuildPattern()
    {
        var rng = new Random(0xB10C);
        var pattern = new (sbyte, sbyte, sbyte, sbyte)[256];
        for (var i = 0; i < 256; i++)
        {
            pattern[i] = (Gauss(rng), Gauss(rng), Gauss(rng), Gauss(rng));
        }

        return pattern;
    }

    private static sbyte Gauss(Random rng)
    {
        // Box-Muller, sigma ~= 6, clamped to a 31x31 patch.
        var u1 = 1.0 - rng.NextDouble();
        var u2 = 1.0 - rng.NextDouble();
        var g = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2) * 6.0;
        return (sbyte)Math.Clamp((int)Math.Round(g), -15, 15);
    }
}
