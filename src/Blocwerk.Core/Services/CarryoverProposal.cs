namespace Blocwerk.Core.Services;

/// <summary>
/// A proposed carryover: an old live hold matched to a staged new-centre detected hold, so the
/// old hold's identity (and therefore its boulders) can survive the photo replacement.
/// </summary>
/// <param name="OldHoldId">The old live hold on the wall being replaced.</param>
/// <param name="NewHoldId">The staged new-centre detected hold it matched to.</param>
/// <param name="Confidence">Matcher confidence 0..1.</param>
/// <param name="Moved">True when the geometric residual suggests the hold physically moved.</param>
/// <param name="ResidualPx">Warp-field prediction error, in the new-centre image's pixels.</param>
public record CarryoverProposal(
    Guid OldHoldId,
    Guid NewHoldId,
    double Confidence,
    bool Moved,
    double ResidualPx);
