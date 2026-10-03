using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Detection.Outlines;

/// <summary>One eligible hold, its fresh outline and the verdict.</summary>
/// <param name="Hold">The hold as it was read (a snapshot; the apply step re-checks it before writing).</param>
/// <param name="Outcome">The verdict.</param>
/// <param name="Result">The outliner's result (its fingerprint is used even when the hold stays a circle).</param>
public sealed record HoldOutlineUpgradeProposal(Hold Hold, HoldOutlineUpgradeOutcome Outcome, HoldOutlineResult Result)
{
    /// <summary>Gets a value indicating whether the new outline carries a pocket / through-hole.</summary>
    public bool HasHoles => Outcome == HoldOutlineUpgradeOutcome.Outline && Result.ShapeHoles is { Count: > 0 };

    /// <summary>
    /// Gets a value indicating whether the hold gets a fingerprint: it has none yet and the result is not a
    /// rejected leak (whose fingerprint describes the wrong pixels).
    /// </summary>
    public bool FillsFingerprint => Hold.FingerprintJson is null && Outcome != HoldOutlineUpgradeOutcome.RejectedLeak;
}
