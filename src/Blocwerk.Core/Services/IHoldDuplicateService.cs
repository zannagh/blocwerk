using Blocwerk.Core.Detection.Outlines;

namespace Blocwerk.Core.Services;

/// <summary>
/// The wall-admin review list of possible duplicate holds: the same physical hold recorded twice. Each suggestion can be
/// merged into one hold (boulders move across; one journal batch per merge, exactly revertable), or dismissed as "not a
/// duplicate" (remembered, never suggested again). Owner / wall admin only, never from a kiosk tablet.
/// </summary>
public interface IHoldDuplicateService
{
    /// <summary>One page of suggestions, most confident first, with the counts per kind and the merges that can still be undone.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="skip">How many suggestions to skip.</param>
    /// <param name="take">How many to return.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The page.</returns>
    Task<HoldDuplicatePage> ListAsync(Guid wallId, int skip, int take, CancellationToken ct = default);

    /// <summary>Merges two holds of one photo into one; takes the wall's hold write lock.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="leftId">The "left" hold of the suggestion.</param>
    /// <param name="rightId">The "right" hold.</param>
    /// <param name="mode">Which hold stays and whether it takes the detected shape.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>What happened, with the journal batch to undo it.</returns>
    Task<HoldMergeResult> MergeAsync(Guid wallId, Guid leftId, Guid rightId, HoldMergeMode mode, CancellationToken ct = default);

    /// <summary>Remembers that two holds are different, so the pair is not suggested again.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="holdAId">One hold.</param>
    /// <param name="holdBId">The other.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    Task DismissAsync(Guid wallId, Guid holdAId, Guid holdBId, CancellationToken ct = default);

    /// <summary>Reverts a merge exactly; refuses (and changes nothing) when something it touched was edited since.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="batchId">The batch <see cref="MergeAsync"/> returned.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The journal's revert result.</returns>
    Task<ChangeJournalRevertResult> RevertAsync(Guid wallId, Guid batchId, CancellationToken ct = default);

    /// <summary>A review picture: the panel photo around <paramref name="holdId"/> with both holds' outlines drawn (this one highlighted).</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="holdId">The hold to centre on.</param>
    /// <param name="otherId">The other hold of the pair.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>JPEG bytes, or null when there is no readable photo.</returns>
    Task<byte[]?> CropAsync(Guid wallId, Guid holdId, Guid otherId, CancellationToken ct = default);
}

/// <summary>One hold of a suggestion, as the review list shows it.</summary>
/// <param name="Id">The hold.</param>
/// <param name="Name">Its name, if any.</param>
/// <param name="Color">Its colour, if any.</param>
/// <param name="IsHandMade">Placed or drawn by a person (or virtual).</param>
/// <param name="IsVirtual">A virtual hold (not visible in the photo).</param>
/// <param name="BoulderCount">Active boulders that use it.</param>
/// <param name="BoulderNames">The first few of those boulders' names.</param>
public sealed record HoldDuplicateHoldInfo(
    Guid Id, string? Name, string? Color, bool IsHandMade, bool IsVirtual, int BoulderCount, IReadOnlyList<string> BoulderNames);

/// <summary>One suggestion; <c>Left</c> is the hold the merge rules would keep.</summary>
/// <param name="Kind">Why it was flagged.</param>
/// <param name="Confidence">0..1.</param>
/// <param name="Left">The suggested keeper.</param>
/// <param name="Right">The other hold.</param>
/// <param name="SuggestedMode">The pre-selected merge.</param>
/// <param name="AffectedBoulders">Distinct active boulders that use either hold.</param>
public sealed record HoldDuplicateItem(
    HoldDuplicateKind Kind,
    double Confidence,
    HoldDuplicateHoldInfo Left,
    HoldDuplicateHoldInfo Right,
    HoldMergeMode SuggestedMode,
    int AffectedBoulders);

/// <summary>A merge that can still be undone.</summary>
/// <param name="BatchId">Its journal batch.</param>
/// <param name="CreatedAt">When it was done.</param>
public sealed record HoldMergeRecord(Guid BatchId, DateTimeOffset CreatedAt);

/// <summary>A page of the review list.</summary>
/// <param name="Total">All open suggestions.</param>
/// <param name="ByKind">How many of each kind.</param>
/// <param name="Items">This page.</param>
/// <param name="RecentMerges">The latest merges that can still be undone, newest first.</param>
public sealed record HoldDuplicatePage(
    int Total,
    IReadOnlyDictionary<HoldDuplicateKind, int> ByKind,
    IReadOnlyList<HoldDuplicateItem> Items,
    IReadOnlyList<HoldMergeRecord> RecentMerges);

/// <summary>What a merge did.</summary>
/// <param name="BatchId">The journal batch (undo with it).</param>
/// <param name="KeptId">The hold that stays.</param>
/// <param name="RemovedId">The hold that was deleted.</param>
/// <param name="MovedBoulders">Boulders whose membership moved onto the kept hold.</param>
/// <param name="FlaggedForReview">Boulders that used both holds and were flagged for a check.</param>
public sealed record HoldMergeResult(Guid BatchId, Guid KeptId, Guid RemovedId, int MovedBoulders, int FlaggedForReview);
