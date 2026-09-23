namespace Blocwerk.HoldDetection.Outlines;

/// <summary>
/// The two colours a hold outline is cut between: the local WALL colour, sampled robustly from a ring
/// outside the seed (1.3 r – 1.9 r, keeping the half of the ring closest to its own median so neighbouring
/// holds in the ring do not pollute it), and the HOLD colour, sampled the same way from the seed's core
/// (inside 0.35 r).
/// </summary>
internal sealed class LocalColourModel
{
    /// <summary>Weight on ΔL* in <see cref="Distance2"/>: shadows, chalk and exposure mostly move lightness,
    /// the hold/wall difference lives in chroma.</summary>
    public const float LightnessWeight = 0.5f;

    /// <summary>Core-to-wall contrast below which the core is taken to be showing the wall (a donut's hole,
    /// a bolt-on hold's gap) and the hold colour is re-sampled from the rest of the seed.</summary>
    public const float CoreWallContrast = 9f;

    /// <summary>Max rg-chromaticity distance for a pixel to count as wall material under other lighting.</summary>
    public const float WallChromaTolerance = 0.022f;

    private LocalColourModel(float[] wall, float[] hold, float wallR, float wallG, bool coreIsWall)
    {
        CoreIsWall = coreIsWall;
        Wall = wall;
        Hold = hold;
        WallR = wallR;
        WallG = wallG;
        Contrast = (float)Math.Sqrt(Distance2(hold[0], hold[1], hold[2], wall));
    }

    /// <summary>Gets a value indicating whether the seed core looked like the wall, so <see cref="Hold"/> was
    /// sampled from the most wall-unlike pixels of the seed instead.</summary>
    public bool CoreIsWall { get; }

    /// <summary>Gets the wall Lab (L*, a*, b*).</summary>
    public float[] Wall { get; }

    /// <summary>Gets the hold core Lab (L*, a*, b*).</summary>
    public float[] Hold { get; }

    /// <summary>Gets the wall's red chromaticity.</summary>
    public float WallR { get; }

    /// <summary>Gets the wall's green chromaticity.</summary>
    public float WallG { get; }

    /// <summary>
    /// Gets a value indicating whether the wall is coloured enough (plywood, painted panels) for "darker but same
    /// chromaticity" to mean shadowed wall. On a near-neutral wall a grey or white hold in the shade has exactly
    /// that chromaticity too, so the shadow rule would veto the hold itself; it is off there.
    /// </summary>
    public bool WallHasChroma => Math.Sqrt((Wall[1] * Wall[1]) + (Wall[2] * Wall[2])) >= NeutralWallChroma;

    /// <summary>Lab chroma below which the wall counts as neutral (see <see cref="WallHasChroma"/>).</summary>
    public const float NeutralWallChroma = 6f;

    /// <summary>Gets the weighted distance between hold core and wall: how separable the hold is at all.</summary>
    public float Contrast { get; }

    /// <summary>Samples both colours around the crop's seed.</summary>
    /// <param name="px">The crop's colour planes.</param>
    /// <param name="crop">The crop (for the seed geometry).</param>
    /// <returns>The model, or null when the ring or core has too few pixels.</returns>
    public static LocalColourModel? Sample(CropPixels px, OutlineCrop crop)
    {
        var ring = new List<int>();
        var core = new List<int>();
        var inner = new List<int>();
        double r = crop.Radius;
        int stride = r > 24 ? 2 : 1;
        for (int y = 0; y < px.Height; y += stride)
        {
            for (int x = 0; x < px.Width; x += stride)
            {
                double d = Normalized(crop, x, y);
                if (d <= 0.35)
                {
                    core.Add((y * px.Width) + x);
                }

                if (d <= 0.8)
                {
                    inner.Add((y * px.Width) + x);
                }
                else if (d >= 1.3 && d <= 1.9)
                {
                    ring.Add((y * px.Width) + x);
                }
            }
        }

        if (ring.Count < 20 || core.Count < 5 || r <= 0)
        {
            return null;
        }

        float[] wall = TrimmedMedian(px, ring, 0.5);
        float[] hold = TrimmedMedian(px, core, 0.7);
        var wallPixels = ring.OrderBy(i => Distance2(px.L[i], px.A[i], px.B[i], wall)).Take(ring.Count / 2).ToList();
        float wr = Median(wallPixels.Select(i => px.R[i]).ToList());
        float wg = Median(wallPixels.Select(i => px.G[i]).ToList());
        bool coreIsWall = Distance2(hold[0], hold[1], hold[2], wall) < CoreWallContrast * CoreWallContrast;
        if (coreIsWall)
        {
            // The hold is whatever inside the seed is NOT wall: the 30 % of pixels farthest from it.
            var farthest = inner
                .OrderByDescending(i => Distance2(px.L[i], px.A[i], px.B[i], wall))
                .Take(Math.Max(5, inner.Count * 3 / 10))
                .ToList();
            hold = TrimmedMedian(px, farthest, 0.7);
        }

        return new LocalColourModel(wall, hold, wr, wg, coreIsWall);
    }

    /// <summary>Distance from the seed centre in units of the seed's box ellipse (1 = on the ellipse).</summary>
    /// <param name="crop">The crop.</param>
    /// <param name="x">Pixel X.</param>
    /// <param name="y">Pixel Y.</param>
    /// <returns>The normalized elliptical distance.</returns>
    public static double Normalized(OutlineCrop crop, double x, double y)
    {
        double dx = (x - crop.Centre.X) / crop.RadiusX;
        double dy = (y - crop.Centre.Y) / crop.RadiusY;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    /// <summary>
    /// True when a pixel is DARKER than the wall but keeps its rg-chromaticity: the plywood in shadow (or
    /// the shaded face of a wooden volume). Only darker pixels qualify — the wall on these photos is close
    /// to neutral, so a brighter pixel of wall chromaticity may just as well be a white hold.
    /// </summary>
    /// <param name="px">Colour planes.</param>
    /// <param name="i">Pixel index.</param>
    /// <returns>Whether the pixel looks like wall material.</returns>
    public bool IsWallMaterial(CropPixels px, int i)
    {
        if (!WallHasChroma || px.L[i] >= Wall[0] - 3)
        {
            return false;
        }

        float dr = px.R[i] - WallR;
        float dg = px.G[i] - WallG;
        return (dr * dr) + (dg * dg) < WallChromaTolerance * WallChromaTolerance;
    }

    /// <summary>Share of a mask's pixels that are wall-coloured (within <paramref name="tolerance"/>) or
    /// wall material (<see cref="IsWallMaterial"/>).</summary>
    /// <param name="px">Colour planes.</param>
    /// <param name="mask">Row-major mask bytes (non-zero = inside).</param>
    /// <param name="tolerance">Distance under which a pixel counts as wall.</param>
    /// <returns>The share, 0..1.</returns>
    public float WallLikeShare(CropPixels px, byte[] mask, float tolerance)
    {
        int inside = 0, wallLike = 0;
        float t2 = tolerance * tolerance;
        for (int i = 0; i < mask.Length; i++)
        {
            if (mask[i] == 0)
            {
                continue;
            }

            inside++;
            if (Distance2(px.L[i], px.A[i], px.B[i], Wall) < t2 || IsWallMaterial(px, i))
            {
                wallLike++;
            }
        }

        return inside == 0 ? 1f : (float)wallLike / inside;
    }

    /// <summary>Squared lightness-weighted Lab distance of a pixel colour to a reference.</summary>
    /// <param name="l">L*.</param>
    /// <param name="a">a*.</param>
    /// <param name="b">b*.</param>
    /// <param name="reference">Reference Lab.</param>
    /// <returns>The squared distance.</returns>
    public static float Distance2(float l, float a, float b, float[] reference)
    {
        float dl = LightnessWeight * (l - reference[0]);
        float da = a - reference[1];
        float db = b - reference[2];
        return (dl * dl) + (da * da) + (db * db);
    }

    private static float[] TrimmedMedian(CropPixels px, List<int> idx, double keep)
    {
        var med = new[]
        {
            Median(idx.Select(i => px.L[i]).ToList()),
            Median(idx.Select(i => px.A[i]).ToList()),
            Median(idx.Select(i => px.B[i]).ToList()),
        };
        for (int iter = 0; iter < 2; iter++)
        {
            var kept = idx
                .OrderBy(i => Distance2(px.L[i], px.A[i], px.B[i], med))
                .Take(Math.Max(5, (int)(idx.Count * keep)))
                .ToList();
            med = new[]
            {
                Median(kept.Select(i => px.L[i]).ToList()),
                Median(kept.Select(i => px.A[i]).ToList()),
                Median(kept.Select(i => px.B[i]).ToList()),
            };
        }

        return med;
    }

    private static float Median(List<float> values)
    {
        values.Sort();
        return values[values.Count / 2];
    }
}
