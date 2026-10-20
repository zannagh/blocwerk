// <copyright file="IWallLookOverService.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Refresh;

namespace Blocwerk.Core.LookOver;

/// <summary>
/// "Look over the wall": what an applied panel update was unsure about and nobody answered stays with the wall, the
/// wall is flagged, and a wall admin or moderator works through the findings afterwards. Reading the state is open to
/// every member of the wall; the findings, the answers and finishing are for the wall's owner, admins and moderators
/// (and an installation admin), never from a kiosk tablet.
/// </summary>
public interface IWallLookOverService
{
    /// <summary>Whether the wall waits for a look-over, and how much is open. Null when the user cannot see the wall.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The state.</returns>
    Task<WallLookOverState?> GetStateAsync(Guid wallId, CancellationToken ct = default);

    /// <summary>The findings that are open, and those answered since the wall was flagged, in the order they are worked through.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The findings.</returns>
    Task<IReadOnlyList<LookOverFindingView>> ListAsync(Guid wallId, CancellationToken ct = default);

    /// <summary>Answers one open finding; refuses an answer that does not fit it (<see cref="LookOverFindingView.Answers"/>).</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="findingId">The finding.</param>
    /// <param name="answer">The answer.</param>
    /// <param name="expectedBoulderIds">
    /// The boulders the user was shown on the hold. Required for every answer but <see cref="LookOverAnswer.Settle"/>:
    /// when other boulders use the hold by now, the answer is refused and the user looks again.
    /// </param>
    /// <param name="newHoldBoulderIds">
    /// For <see cref="LookOverAnswer.UseNewHold"/> only, asked per boulder: the boulders that should use the new hold,
    /// a subset of <paramref name="expectedBoulderIds"/>. The others become historic with the old hold, which is
    /// removed; none of them is the answer "the old hold is gone". Null moves every boulder (the merge).
    /// </param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    Task AnswerAsync(
        Guid wallId,
        Guid findingId,
        LookOverAnswer answer,
        IReadOnlyCollection<Guid>? expectedBoulderIds = null,
        IReadOnlyCollection<Guid>? newHoldBoulderIds = null,
        CancellationToken ct = default);

    /// <summary>
    /// Takes back an answer that changed nothing on the wall ("Same hold", "Still there", "Keep…", "Understood"): the
    /// finding is open again, and a review mark the answer took off the hold or its boulders is back. A removed or
    /// merged hold is not undone here.
    /// </summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="findingId">The finding.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    Task UndoAsync(Guid wallId, Guid findingId, CancellationToken ct = default);

    /// <summary>
    /// Settles every open finding of one kind as "it is fine as it is": the unsure matches as "same hold", the old and
    /// new holds on one spot as "both are there". No hold is removed or merged.
    /// </summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="kind"><see cref="LookOverFindingKind.UnsureMatch"/> or <see cref="LookOverFindingKind.SameSpot"/>.</param>
    /// <param name="findingIds">The findings the user was shown and confirmed; only those of them that are still open are settled.</param>
    /// <param name="onlyWithoutBoulders">Of those, only the findings whose hold no current boulder uses (still, now).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many of the confirmed findings were settled.</returns>
    Task<LookOverBulkResult> SettleAllAsync(
        Guid wallId, LookOverFindingKind kind, IReadOnlyCollection<Guid> findingIds, bool onlyWithoutBoulders, CancellationToken ct = default);

    /// <summary>
    /// Finishes the look-over: records who and when and takes the flag off the wall. Refused while findings are open
    /// unless <paramref name="leaveOpen"/> says so (they stay open and listed). Boulders marked for review stay marked.
    /// </summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="leaveOpen">Finish although findings are open.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    Task FinishAsync(Guid wallId, bool leaveOpen, CancellationToken ct = default);

    /// <summary>Whether the wall has this finding (for whoever may look over the wall).</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="findingId">The finding.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when it does.</returns>
    Task<bool> HasFindingAsync(Guid wallId, Guid findingId, CancellationToken ct = default);

    /// <summary>One picture of a finding (the old photo, the new photo or the 3D texture), or null when there is none.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="findingId">The finding.</param>
    /// <param name="view">Which picture.</param>
    /// <param name="plain">Without the ring around the spot: for a picture the page outlines the holds on itself.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>JPEG bytes.</returns>
    Task<byte[]?> GetCropAsync(Guid wallId, Guid findingId, CheckCropView view, bool plain = false, CancellationToken ct = default);

    /// <summary>
    /// Removes, in one step, holds the update could not find and that no current boulder uses: only those of
    /// <paramref name="findingIds"/> that are still open and still so. One journal batch, so it can be reverted as one.
    /// </summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="findingIds">The findings the user was shown and confirmed.</param>
    /// <param name="onlyCovered">Only the holds that lie under a new hold.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many of the confirmed holds were removed.</returns>
    Task<LookOverBulkResult> RemoveUnusedAsync(Guid wallId, IReadOnlyCollection<Guid> findingIds, bool onlyCovered, CancellationToken ct = default);

    /// <summary>
    /// Adds, in one step, discarded detections as holds: only those of <paramref name="findingIds"/> that are still
    /// open, that the detector was sure of (<see cref="LookOverRules.SureDetection"/>) and that overlap no live hold
    /// when it runs. Each becomes a hold as with <see cref="LookOverAnswer.AddHold"/>; one journal batch.
    /// </summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="findingIds">The findings the user was shown and confirmed.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many of the confirmed detections were added.</returns>
    Task<LookOverBulkResult> AddDetectionsAsync(Guid wallId, IReadOnlyCollection<Guid> findingIds, CancellationToken ct = default);

    /// <summary>
    /// "It is this hold": merges the kept hold of a finding into <paramref name="targetHoldId"/>, a hold of the same
    /// photo that is not itself kept. Wall admins only (it is the duplicate review's merge, and is undone there).
    /// </summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="findingId">The finding.</param>
    /// <param name="targetHoldId">The hold on the new photo that the old hold really is.</param>
    /// <param name="expectedBoulderIds">The boulders the user was shown on the old hold.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    Task ReassignAsync(Guid wallId, Guid findingId, Guid targetHoldId, IReadOnlyCollection<Guid> expectedBoulderIds, CancellationToken ct = default);

    /// <summary>
    /// "It is here": moves the kept hold of a finding to a spot on its photo (the hold editor's move), keeping its
    /// outline; the hold is confirmed by that and marked for review so the outline can be refined.
    /// </summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="findingId">The finding.</param>
    /// <param name="x">The spot across the photo, 0..1.</param>
    /// <param name="y">The spot down the photo, 0..1.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    Task MoveAsync(Guid wallId, Guid findingId, double x, double y, CancellationToken ct = default);

    /// <summary>
    /// The live holds of a finding's photo. Those that are not <see cref="Services.PanelHold.Kept"/> are what an old
    /// hold can be reassigned to; the kept ones are there so the page can draw the one it asks about.
    /// </summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="panelId">The panel photo.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The holds.</returns>
    Task<IReadOnlyList<Services.PanelHold>> GetPanelHoldsAsync(Guid wallId, Guid panelId, CancellationToken ct = default);
}
