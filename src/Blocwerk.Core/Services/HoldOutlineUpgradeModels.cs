namespace Blocwerk.Core.Services;

/// <summary>Scope of an outline upgrade.</summary>
/// <param name="IncludeManual">Also outline manually placed holds (off by default: their radius is often a placeholder).</param>
public sealed record HoldOutlineUpgradeOptions(bool IncludeManual = false);

/// <summary>Whether the action can run, and the wall's latest run.</summary>
/// <param name="Enabled">False when outline detection is switched off or not installed on this host.</param>
/// <param name="LatestRun">The newest run on the wall, or null.</param>
public sealed record HoldOutlineUpgradeStatus(bool Enabled, HoldOutlineUpgradeRunInfo? LatestRun);

/// <summary>A run, as the admin UI shows it.</summary>
/// <param name="Id">Run id.</param>
/// <param name="CreatedAt">When it ran.</param>
/// <param name="Outlined">Holds that got an outline.</param>
/// <param name="Fingerprinted">Holds that got a fingerprint.</param>
/// <param name="RevertedAt">When it was reverted, or null.</param>
public sealed record HoldOutlineUpgradeRunInfo(
    Guid Id, DateTimeOffset CreatedAt, int Outlined, int Fingerprinted, DateTimeOffset? RevertedAt);

/// <summary>What a dry run found.</summary>
public sealed record HoldOutlineUpgradePreview
{
    /// <summary>Gets the number of photos looked at.</summary>
    public int Photos { get; init; }

    /// <summary>Gets the circle holds in scope.</summary>
    public int Eligible { get; init; }

    /// <summary>Gets the holds that would get an outline (<see cref="ByContour"/> + <see cref="ByGrabCut"/>).</summary>
    public int WouldOutline => ByContour + ByGrabCut;

    /// <summary>Gets the outlines found by colour segmentation.</summary>
    public int ByContour { get; init; }

    /// <summary>Gets the outlines found by the GrabCut retry.</summary>
    public int ByGrabCut { get; init; }

    /// <summary>Gets the holds that would stay circles (outliner gave up, or a manual hold's result looked like a leak).</summary>
    public int WouldKeepCircle { get; init; }

    /// <summary>Gets the manual holds whose outline was dropped as a leak (included in <see cref="WouldKeepCircle"/>).</summary>
    public int RejectedAsLeak { get; init; }

    /// <summary>Gets the outlined holds with a pocket / through-hole.</summary>
    public int WithHoles { get; init; }

    /// <summary>Gets the holds that would get a fingerprint.</summary>
    public int WouldFingerprint { get; init; }

    /// <summary>Gets a few holds that would be outlined, for spot checks.</summary>
    public IReadOnlyList<Guid> ExampleHoldIds { get; init; } = [];
}

/// <summary>What an apply wrote.</summary>
/// <param name="RunId">The recorded run (revert it with this id).</param>
/// <param name="Eligible">Circle holds in scope.</param>
/// <param name="Outlined">Holds that got an outline.</param>
/// <param name="KeptCircle">Holds that stayed circles.</param>
/// <param name="WithHoles">Outlined holds with a pocket / through-hole.</param>
/// <param name="Fingerprinted">Holds that got a fingerprint.</param>
/// <param name="Measured">Holds that got millimetre sizes (marker walls with a model only).</param>
/// <param name="SkippedChanged">Holds edited while the run was working, left alone.</param>
public sealed record HoldOutlineUpgradeResult(
    Guid RunId, int Eligible, int Outlined, int KeptCircle, int WithHoles, int Fingerprinted, int Measured, int SkippedChanged);

/// <summary>What a revert restored.</summary>
/// <param name="Reverted">Holds restored to what they were before the run.</param>
/// <param name="SkippedEdited">Holds edited since the run, left as they are.</param>
/// <param name="Missing">Holds deleted since the run.</param>
public sealed record HoldOutlineRevertResult(int Reverted, IReadOnlyList<Guid> SkippedEdited, int Missing);
