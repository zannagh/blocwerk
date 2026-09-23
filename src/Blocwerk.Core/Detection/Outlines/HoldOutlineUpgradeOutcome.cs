namespace Blocwerk.Core.Detection.Outlines;

/// <summary>What the outline upgrade would do with one circle hold.</summary>
public enum HoldOutlineUpgradeOutcome
{
    /// <summary>A real contour replaces the circle.</summary>
    Outline,

    /// <summary>The outliner gave up (circle fallback): the hold stays a circle.</summary>
    KeepCircle,

    /// <summary>A manual hold's outline looked like a leak and is dropped: the hold stays a circle.</summary>
    RejectedLeak,
}
