namespace Blocwerk.Core.Detection.Outlines;

/// <summary>A proposed "this is the same physical hold, moved" pair.</summary>
/// <param name="DisappearedHoldId">The hold that vanished from the old generation.</param>
/// <param name="AppearedHoldId">The unmatched new hold proposed to be the same hold.</param>
/// <param name="Score">The fingerprint similarity of the pair (0..1).</param>
/// <param name="Margin">How far the pair's score beats the best competing pair that shares either hold
/// (1 when there is no competitor).</param>
public sealed record HoldRelocationProposal(Guid DisappearedHoldId, Guid AppearedHoldId, double Score, double Margin);
