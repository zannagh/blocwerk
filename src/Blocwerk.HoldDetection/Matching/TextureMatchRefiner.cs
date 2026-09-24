using Blocwerk.Core.Geometry;
using OpenCvSharp;

namespace Blocwerk.HoldDetection.Matching;

/// <summary>
/// Guided matching, grown: the fine stage searches only 40 mm around where the coarse homography puts each
/// feature, so a coarse fit that is right in one corner and off elsewhere yields matches in that corner only
/// (The Attic's right panel photo: 146 inliers over 8 % of the main wall). Refitting the homography on those
/// matches and matching again extends the right region outward; it stops when the consensus stops growing.
/// The acceptance in Core judges the final pairs exactly as before.
/// </summary>
internal static class TextureMatchRefiner
{
    /// <summary>Most rematch rounds after the first fine match.</summary>
    internal const int MaxRounds = 3;

    /// <summary>Fewest RANSAC inliers a refit needs before it may steer the next round.</summary>
    internal const int MinSteeringInliers = 30;

    /// <summary>Plane-space RANSAC threshold of the refit (as the Core acceptance's inlier threshold).</summary>
    internal const double ThresholdMm = 8;

    /// <summary>The fine pairs, rematched while the refitted homography gains inliers.</summary>
    /// <param name="photo">The photo, 8-bit gray, full resolution.</param>
    /// <param name="texture">The texture, 8-bit gray, full resolution.</param>
    /// <param name="mask">The texture's coverage mask, or null.</param>
    /// <param name="h">The starting homography, photo px → texture px.</param>
    /// <param name="textureMmPerPx">The texture's resolution.</param>
    /// <param name="firstRadiusMm">The first round's search radius (later rounds use the fine default).</param>
    /// <returns>The best round's pairs.</returns>
    public static List<PointCorrespondence> Match(
        Mat photo, Mat texture, Mat? mask, double[,] h, double textureMmPerPx, double firstRadiusMm = TextureFineMatcher.SearchRadiusMm)
    {
        var pairs = TextureFineMatcher.Match(photo, texture, mask, h, textureMmPerPx, firstRadiusMm);
        var (fit, inliers) = Refit(pairs, textureMmPerPx);
        for (var round = 0; round < MaxRounds && fit is not null && inliers >= MinSteeringInliers; round++)
        {
            var next = TextureFineMatcher.Match(photo, texture, mask, fit, textureMmPerPx);
            var (nextFit, nextInliers) = Refit(next, textureMmPerPx);
            if (nextInliers <= inliers * 1.02)
            {
                break;
            }

            (pairs, fit, inliers) = (next, nextFit, nextInliers);
        }

        return pairs;
    }

    /// <summary>RANSAC homography over the pairs (photo px → texture px) and its inlier count.</summary>
    internal static (double[,]? H, int Inliers) Refit(IReadOnlyList<PointCorrespondence> pairs, double textureMmPerPx)
    {
        if (pairs.Count < 8)
        {
            return (null, 0);
        }

        var src = pairs.Select(p => new Point2d(p.SrcX, p.SrcY)).ToList();
        var dst = pairs.Select(p => new Point2d(p.DstX, p.DstY)).ToList();
        using var mask = new Mat();
        using var h = Cv2.FindHomography(src, dst, HomographyMethods.Ransac, ThresholdMm / textureMmPerPx, mask, 4000, 0.999);
        return h.Empty() ? (null, 0) : (HomographyHelper.ToArray(h), Cv2.CountNonZero(mask));
    }
}
