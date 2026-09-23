using OpenCvSharp;

namespace Blocwerk.HoldDetection.Outlines;

/// <summary>
/// Finds the significant interior holes of an accepted hold outline — a donut's through-hole showing the
/// plywood, or a deep pocket in shadow — instead of filling them. Conservative by design: a missing hole
/// only costs looks, a wrong hole punches a gap into a solid hold, so every test below must pass.
/// </summary>
/// <remarks>
/// A hole is a child contour (RETR_CCOMP) of the seed component's outer ring, and it must pass all of these.
/// <list type="bullet">
/// <item>covers ≥ <see cref="MinAreaShare"/> and ≤ <see cref="MaxAreaShare"/> of the outer area and ≥
/// <see cref="MinAreaPx"/> working pixels (smaller gaps are chalk, bolt holes, segmentation noise);</item>
/// <item>does not look like the hold: its median colour is at least <see cref="MinHoldDistance"/> away from
/// the hold colour; and</item>
/// <item>looks like wall (≥ <see cref="MinWallShare"/> of its pixels wall-coloured or wall material) OR is
/// markedly darker than both hold and wall (≥ <see cref="MinDarkening"/> L*) — the floor of a deep pocket.</item>
/// </list>
/// </remarks>
internal static class HoleFinder
{
    /// <summary>Vertex budget of one hole ring.</summary>
    public const int MaxHoleVertices = 16;

    /// <summary>Smallest hole, as a share of the outer ring's area.</summary>
    public const double MinAreaShare = 0.035;

    /// <summary>Largest hole, as a share of the outer ring's area: beyond this it is a ring-shaped leak.</summary>
    public const double MaxAreaShare = 0.6;

    /// <summary>Smallest hole in working pixels.</summary>
    public const double MinAreaPx = 20;

    /// <summary>Share of wall-like pixels that makes a hole a through-hole showing the wall.</summary>
    public const double MinWallShare = 0.5;

    /// <summary>L* by which a pocket must be darker than both the hold and the wall to count as a deep pocket.</summary>
    public const float MinDarkening = 15f;

    /// <summary>Minimum (lightness-weighted) Lab distance of a hole's median colour to the hold colour.</summary>
    public const float MinHoldDistance = 9f;

    /// <summary>Returns the qualifying hole rings (simplified, working pixels) of the seed component.</summary>
    /// <param name="mask">The cleaned segmentation mask (not modified).</param>
    /// <param name="outer">The accepted outer contour.</param>
    /// <param name="px">The crop's colour planes.</param>
    /// <param name="model">The local wall/hold colours.</param>
    /// <returns>The hole rings; empty for a solid hold.</returns>
    public static IReadOnlyList<Point[]> Find(Mat mask, Point[] outer, CropPixels px, LocalColourModel model)
    {
        using Mat filled = MaskOps.Fill(outer, mask.Width, mask.Height);
        using var component = new Mat();
        Cv2.BitwiseAnd(mask, filled, component);
        Cv2.FindContours(component, out Point[][] contours, out HierarchyIndex[] hierarchy, RetrievalModes.CComp, ContourApproximationModes.ApproxNone);

        double outerArea = Cv2.ContourArea(outer);
        var holes = new List<Point[]>();
        for (int i = 0; i < contours.Length; i++)
        {
            // CCOMP: top level = outer boundaries, their children = holes.
            if (hierarchy[i].Parent < 0 || contours[i].Length < 3)
            {
                continue;
            }

            double area = Cv2.ContourArea(contours[i]);
            if (area < MinAreaPx || area < MinAreaShare * outerArea || area > MaxAreaShare * outerArea)
            {
                continue;
            }

            if (LooksLikeHole(contours[i], component, px, model))
            {
                holes.Add(MaskOps.Simplify(contours[i], MaxHoleVertices));
            }
        }

        return holes;
    }

    private static bool LooksLikeHole(Point[] ring, Mat component, CropPixels px, LocalColourModel model)
    {
        byte[] inside = HolePixels(ring, component);
        var idx = new List<int>();
        for (int i = 0; i < inside.Length; i++)
        {
            if (inside[i] != 0)
            {
                idx.Add(i);
            }
        }

        if (idx.Count < MinAreaPx)
        {
            return false;
        }

        float l = Median(idx.Select(i => px.L[i]));
        float a = Median(idx.Select(i => px.A[i]));
        float b = Median(idx.Select(i => px.B[i]));
        if (LocalColourModel.Distance2(l, a, b, model.Hold) < MinHoldDistance * MinHoldDistance)
        {
            return false;
        }

        bool wallLike = model.WallLikeShare(px, inside, (float)OpenCvHoldOutlineSession.MinContrast) >= MinWallShare;
        bool deepPocket = l <= model.Hold[0] - MinDarkening && l <= model.Wall[0] - MinDarkening;
        return wallLike || deepPocket;
    }

    /// <summary>The ring's interior minus any hold island inside it: the pixels that actually show the hole.</summary>
    private static byte[] HolePixels(Point[] ring, Mat component)
    {
        using Mat hole = MaskOps.Fill(ring, component.Width, component.Height);
        using var notHold = new Mat();
        Cv2.BitwiseNot(component, notHold);
        Cv2.BitwiseAnd(hole, notHold, hole);
        return CropPixels.ToBytes(hole);
    }

    private static float Median(IEnumerable<float> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted[sorted.Count / 2];
    }
}
