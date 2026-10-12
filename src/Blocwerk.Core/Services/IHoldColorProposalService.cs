// <copyright file="IHoldColorProposalService.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Services;

/// <summary>How many of a wall's live holds have no colour, and how many carry an unconfirmed proposal.</summary>
/// <param name="WithoutColor">Live holds without a colour.</param>
/// <param name="Proposed">Live holds whose colour is still the detection's proposal.</param>
public sealed record HoldColorProposalStatus(int WithoutColor, int Proposed);

/// <summary>
/// The wall-admin action "Suggest colours for holds without one": runs <see cref="IHoldColorProposer"/> over every live
/// hold of the wall that has no colour, in one journal batch. Safe to repeat: a hold that has a colour is never touched,
/// and a hold the estimate is not sure about stays without one. Wall admin only, never from a kiosk tablet.
/// </summary>
public interface IHoldColorProposalService
{
    /// <summary>Reads the counts the settings section shows.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The counts.</returns>
    Task<HoldColorProposalStatus> GetStatusAsync(Guid wallId, CancellationToken ct = default);

    /// <summary>Proposes colours for the wall's live holds without one; takes the hold write lock.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many holds got a colour and how many stayed unset.</returns>
    Task<HoldColorProposalReport> SuggestMissingAsync(Guid wallId, CancellationToken ct = default);
}
