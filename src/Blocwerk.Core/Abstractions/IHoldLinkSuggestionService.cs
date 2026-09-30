// <copyright file="IHoldLinkSuggestionService.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.HoldLinks;

namespace Blocwerk.Core.Abstractions;

/// <summary>
/// "These look like the same hold on two photos": holds of different panel photos whose 3D positions coincide but
/// that are not linked (<see cref="HoldLinkSuggestionFinder"/>), kept as pending suggestions for wall admins.
/// Never links by itself: <see cref="LinkAsync"/> goes through the normal linking path, <see cref="RejectAsync"/> is remembered.
/// </summary>
public interface IHoldLinkSuggestionService
{
    /// <summary>Finds the suggestions again and stores them (pipeline, no user); never throws.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many are pending, or null when the wall has no usable 3D model or it failed.</returns>
    Task<int?> RefreshFromPipelineAsync(Guid wallId, CancellationToken ct = default);

    /// <summary>Whether the current user may review suggestions on the wall (a wall admin, not on a kiosk tablet); never throws.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True for a wall admin.</returns>
    Task<bool> CanReviewAsync(Guid wallId, CancellationToken ct = default);

    /// <summary>
    /// How many suggestions wait for review, re-checked against the wall as it is now. 0 for anyone who is not a
    /// wall admin, on a kiosk tablet, when the wall has no 3D model, and when the check fails (logged); never throws
    /// except on cancellation.
    /// </summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The count.</returns>
    Task<int> CountPendingAsync(Guid wallId, CancellationToken ct = default);

    /// <summary>The pending suggestions with both holds' photo positions, closest first (wall admins).</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The suggestions.</returns>
    Task<IReadOnlyList<HoldLinkSuggestionView>> ListAsync(Guid wallId, CancellationToken ct = default);

    /// <summary>Links the two holds as one (the same link the overlaps step creates) and drops the suggestion.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="holdAId">One hold.</param>
    /// <param name="holdBId">The other.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    Task LinkAsync(Guid wallId, Guid holdAId, Guid holdBId, CancellationToken ct = default);

    /// <summary>Remembers that the two holds are not the same, so the pair is not suggested again.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="holdAId">One hold.</param>
    /// <param name="holdBId">The other.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task.</returns>
    Task RejectAsync(Guid wallId, Guid holdAId, Guid holdBId, CancellationToken ct = default);
}
