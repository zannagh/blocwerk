namespace Blocwerk.Core.Services;

/// <summary>
/// A suggested carryover mapping: an old live hold the matcher thinks corresponds to a staged
/// new-centre detected hold, so the old hold's identity (and therefore its boulders) can survive the
/// photo replacement. This is a SUGGESTION ONLY — the review layer seeds every old hold as
/// <see cref="Blocwerk.Core.Enums.CarryKind.Carried"/> by default and merely pre-fills
/// <see cref="NewHoldId"/> from these. The matcher never asserts a hold has changed; "changed" is
/// a manual decision made in the UI.
/// </summary>
/// <param name="OldHoldId">The old live hold on the wall being replaced.</param>
/// <param name="NewHoldId">The staged new-centre detected hold it is suggested to map to.</param>
/// <param name="Confidence">Matcher confidence 0..1 (informational; strength of the suggestion).</param>
/// <param name="ResidualPx">Warp-field prediction error, in the new-centre image's pixels (informational).</param>
public record CarryoverProposal(
    Guid OldHoldId,
    Guid NewHoldId,
    double Confidence,
    double ResidualPx);
