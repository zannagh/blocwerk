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
/// <para>
/// Two steps, because reading the photos takes seconds: <see cref="PlanAsync"/> only reads and may run without the
/// wall's hold lock; <see cref="ApplyAsync"/> is quick and reads each hold's colour again before it writes, so a colour
/// somebody set in between is kept.
/// </para>
/// </summary>
public interface IHoldColorProposer
{
    /// <summary>Works out the colours without changing or tracking anything.</summary>
    /// <param name="db">The context.</param>
    /// <param name="wallId">The wall.</param>
    /// <param name="onlyHoldIds">When set, only these holds (and the holds linked to them); otherwise every live hold.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The plan.</returns>
    Task<HoldColorPlan> PlanAsync(
        BlocwerkDbContext db, Guid wallId, IReadOnlyCollection<Guid>? onlyHoldIds = null, CancellationToken ct = default);

    /// <summary>
    /// Writes a plan onto tracked holds; the caller saves. A hold that is no longer live, has a colour by now, or whose
    /// material does not come in the planned colour is left as it is.
    /// </summary>
    /// <param name="db">The context; changes are left unsaved.</param>
    /// <param name="wallId">The wall.</param>
    /// <param name="plan">The plan of <see cref="PlanAsync"/>.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>What was written and what stayed unset.</returns>
    Task<HoldColorProposalReport> ApplyAsync(BlocwerkDbContext db, Guid wallId, HoldColorPlan plan, CancellationToken ct = default);
}
