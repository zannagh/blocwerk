// <copyright file="IHoldColorProposer.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;

namespace Blocwerk.Core.Services;

/// <summary>
/// Proposes a colour for live holds that have none, from the panel photo each sits on
/// (<see cref="Holds.Colors.HoldColorEstimator"/>). A proposal is written to <see cref="Entities.Hold.Color"/> and
/// <see cref="Entities.Hold.ProposedColor"/>; a hold that has a colour is never touched. Holds linked across panels get
/// one colour (<see cref="Holds.Colors.LinkedHoldColorRule"/>), or the colour a linked hold already has.
/// </summary>
public interface IHoldColorProposer
{
    /// <summary>Proposes colours on tracked holds; the caller saves.</summary>
    /// <param name="db">The context; changes are left unsaved.</param>
    /// <param name="wallId">The wall.</param>
    /// <param name="onlyHoldIds">When set, only these holds (and the holds linked to them); otherwise every live hold.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>What was proposed and what stayed unset.</returns>
    Task<HoldColorProposalReport> ProposeAsync(
        BlocwerkDbContext db, Guid wallId, IReadOnlyCollection<Guid>? onlyHoldIds = null, CancellationToken ct = default);
}
