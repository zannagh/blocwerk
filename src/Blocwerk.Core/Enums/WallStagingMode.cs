namespace Blocwerk.Core.Enums;

public enum WallStagingMode
{
    None = 0,
    Detected = 1,
    Manual = 2,

    /// <summary>Full rebuild: the previous hold model is replaced rather than carried forward.</summary>
    Recreate = 3,

    /// <summary>
    /// The staged photo pair was produced by the stitch sidecar from several handheld photos,
    /// and the staged holds were transferred onto it by the sidecar's matcher.
    /// </summary>
    Stitched = 4,
}
