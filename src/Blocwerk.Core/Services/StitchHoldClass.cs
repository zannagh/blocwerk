namespace Blocwerk.Core.Services;

/// <summary>
/// The pipeline's carryover verdict for one hold: what happened to it between the old wall photo
/// and the new one.
/// </summary>
internal enum StitchHoldClass
{
    /// <summary>
    /// An existing hold that was found again on the new image. The clone keeps its source hold's
    /// identity link; how far the match landed from the prediction decides whether it needs review.
    /// </summary>
    CarriedOver,

    /// <summary>
    /// An existing hold that was NOT found on the new image. The clone is still created, at its
    /// predicted position and flagged, so its boulder links survive.
    /// </summary>
    Missing,

    /// <summary>
    /// A hold detected on the new image that matches nothing in the old set. Created fresh:
    /// auto-detected, no source hold, always flagged for review.
    /// </summary>
    New,
}
