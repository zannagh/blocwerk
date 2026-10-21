// <copyright file="IWallEnhancementService.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Enhancement;

/// <summary>
/// "Enhance this wall": runs every enhancing step once for the wall's current generation, lets an admin review what
/// they did, and records the finalized enhancement so the generation is never enhanced twice. Wall admins only, never
/// from a kiosk tablet or a share link.
/// </summary>
public interface IWallEnhancementService
{
    /// <summary>The current generation's enhancement.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Its state.</returns>
    Task<WallEnhancementView> GetAsync(Guid wallId, CancellationToken ct = default);

    /// <summary>
    /// Starts the enhancement of the current generation in the background, or continues a stopped one. Refused when the
    /// generation was already enhanced (or waits for its review), and while a wall update is open.
    /// </summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Its state after starting.</returns>
    Task<WallEnhancementView> StartAsync(Guid wallId, CancellationToken ct = default);

    /// <summary>A page of the current run's review items of one kind, pending first.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="kind">The kind.</param>
    /// <param name="pendingOnly">Only the unanswered ones.</param>
    /// <param name="skip">Items to skip.</param>
    /// <param name="take">Items to return (at most 100).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The page.</returns>
    Task<WallEnhancementItemPage> ListItemsAsync(
        Guid wallId, WallEnhancementItemKind kind, bool pendingOnly, int skip, int take, CancellationToken ct = default);

    /// <summary>Answers one item: keep / carry out (<paramref name="accept"/>) or undo / turn down.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="itemId">The item.</param>
    /// <param name="accept">True to keep or carry out.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>What it did.</returns>
    Task<WallEnhancementDecisionResult> DecideAsync(Guid wallId, Guid itemId, bool accept, CancellationToken ct = default);

    /// <summary>Accepts every pending item of a kind.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="kind">The kind.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many were accepted, and how many could not be carried out.</returns>
    Task<(int Accepted, int Skipped)> AcceptAllAsync(Guid wallId, WallEnhancementItemKind kind, CancellationToken ct = default);

    /// <summary>
    /// Records the reviewed enhancement for the generation. Refused while an item is pending, and while a step failed
    /// unless <paramref name="withoutFailedSteps"/> says the admin chose to finalize without it.
    /// </summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="withoutFailedSteps">True when the admin confirmed finalizing without the failed steps.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The finalized state.</returns>
    Task<WallEnhancementView> FinalizeAsync(Guid wallId, bool withoutFailedSteps = false, CancellationToken ct = default);

    /// <summary>
    /// Cancels the running enhancement: the job stops at its next await, the step in hand is recorded as failed and the
    /// review opens (where the step can be retried or the run discarded). A job that does not stop within a few seconds,
    /// or is not live at all, is marked so directly.
    /// </summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The state after cancelling.</returns>
    Task<WallEnhancementView> CancelAsync(Guid wallId, CancellationToken ct = default);

    /// <summary>Runs a failed step of the current run again (the run goes back to running for it).</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="stepKey">The failed step.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The state after starting it.</returns>
    Task<WallEnhancementView> RetryStepAsync(Guid wallId, string stepKey, CancellationToken ct = default);

    /// <summary>
    /// Discards the current run before it is finalized: takes back what its steps and its answers wrote through the change
    /// journal (where nobody changed it since) and removes the run, so the generation can be enhanced again.
    /// </summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>What was taken back and what stayed.</returns>
    Task<WallEnhancementDiscardResult> DiscardAsync(Guid wallId, CancellationToken ct = default);

    /// <summary>The review picture of an item (JPEG), or null when there is none.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="itemId">The item.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The picture.</returns>
    Task<byte[]?> CropAsync(Guid wallId, Guid itemId, CancellationToken ct = default);

    /// <summary>
    /// The review picture of an item's other hold (JPEG) on that hold's own panel photo, or null when the item has no
    /// other hold. A link pair's two holds sit on two panels, so one picture can never show both.
    /// </summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="itemId">The item.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The picture.</returns>
    Task<byte[]?> OtherCropAsync(Guid wallId, Guid itemId, CancellationToken ct = default);
}
