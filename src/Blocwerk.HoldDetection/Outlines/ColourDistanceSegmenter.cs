using OpenCvSharp;

namespace Blocwerk.HoldDetection.Outlines;

/// <summary>
/// First-choice segmentation: a pixel belongs to the hold when it is far enough from the local wall
/// colour (lightness-weighted Lab distance) AND is not wall material under different light. Shadows (the
/// tell of the 45° overhang, where every hold casts one onto the plywood) and the faces of wooden volumes
/// keep the wall's rg-chromaticity while changing lightness, so both are rejected by the same test.
/// </summary>
internal static class ColourDistanceSegmenter
{
    /// <summary>How much farther from the hold colour than from the wall a pixel may be and still count as hold
    /// (slack for chalk and shading on the hold itself).</summary>
    public const float HoldBias = 1.25f;

    /// <summary>Returns the raw foreground mask (0/255) at a threshold of <paramref name="thresholdFactor"/> × contrast.</summary>
    /// <param name="px">Colour planes.</param>
    /// <param name="model">The sampled wall/hold colours.</param>
    /// <param name="thresholdFactor">Fraction of the hold/wall contrast a pixel must differ from the wall by.</param>
    /// <returns>The mask as a Mat (caller disposes).</returns>
    public static Mat Segment(CropPixels px, LocalColourModel model, double thresholdFactor)
    {
        float t = (float)Math.Clamp(thresholdFactor * model.Contrast, 6.0, 15.0 * thresholdFactor / 0.45);
        float t2 = t * t;
        var mask = new byte[px.L.Length];
        for (int i = 0; i < mask.Length; i++)
        {
            float dWall2 = LocalColourModel.Distance2(px.L[i], px.A[i], px.B[i], model.Wall);
            if (dWall2 < t2 || model.IsWallMaterial(px, i))
            {
                continue;
            }

            // Off-wall is not enough: the pixel must also be (roughly) nearer the hold colour than the wall, or a
            // hold would swallow a touching neighbour of another colour (or a black-and-white marker).
            if (LocalColourModel.Distance2(px.L[i], px.A[i], px.B[i], model.Hold) < HoldBias * HoldBias * dWall2)
            {
                mask[i] = 255;
            }
        }

        return CropPixels.ToMat(mask, px.Width, px.Height);
    }
}
