namespace Blocwerk.Core.Abstractions;

/// <summary>
/// Which edge of the left image overlaps the right image. The matcher recovers the
/// overlap geometrically (coarse homography + local warp field), so this is an advisory
/// hint about capture order rather than a required input.
/// </summary>
public enum HoldOverlapDirection
{
    /// <summary>The right edge of the left image overlaps the right image (default pan L→R).</summary>
    Right,

    /// <summary>The left edge of the left image overlaps the right image.</summary>
    Left,

    /// <summary>The top edge of the left image overlaps the right image.</summary>
    Up,

    /// <summary>The bottom edge of the left image overlaps the right image.</summary>
    Down,
}

/// <summary>
/// Matches holds that appear in two overlapping photos of the same climbing wall,
/// favouring precision: a wrong "same" proposal costs a user confirmation, a missed
/// match merely becomes a "new hold". Ported from the validated Python R&amp;D pipeline
/// (detect → locate overlap → local warp field → geometric match).
/// </summary>
public interface IHoldOverlapMatcher
{
    /// <summary>
    /// Proposes correspondences between the holds of two overlapping wall photos.
    /// </summary>
    /// <param name="leftImage">Encoded bytes (JPEG/PNG) of the left photo.</param>
    /// <param name="leftHolds">Detected holds on the left photo (normalized centres).</param>
    /// <param name="rightImage">Encoded bytes (JPEG/PNG) of the right photo.</param>
    /// <param name="rightHolds">Detected holds on the right photo (normalized centres).</param>
    /// <param name="direction">Advisory overlap direction; the geometry is recovered regardless.</param>
    /// <returns>One-to-one proposals plus the unmatched-in-band buckets.</returns>
    HoldOverlapResult Match(
        byte[] leftImage,
        IReadOnlyList<MatcherHold> leftHolds,
        byte[] rightImage,
        IReadOnlyList<MatcherHold> rightHolds,
        HoldOverlapDirection direction);
}
