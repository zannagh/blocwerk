// <copyright file="HoldMovePlan.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Security.Cryptography;
using System.Text;

namespace Blocwerk.Core.HoldMoves;

/// <summary>
/// The moves a promote would act on: every carried pair whose measurement says it moved. Covered by the confirm
/// screen's decisions fingerprint (<see cref="Version"/>) and re-derived by the promote, which refuses when it differs
/// from what was confirmed, so Apply promotes exactly what the user saw.
/// </summary>
/// <param name="Moves">The moves, ordered by old hold id.</param>
/// <param name="NotFound">
/// Old holds carried in place without a twin whose boulders are marked for review: not found again on the new photo
/// (<see cref="Enums.HoldMoveOutcome.NotFound"/>) or replaced by another hold (<see cref="Enums.HoldMoveOutcome.Replaced"/>).
/// </param>
public sealed record HoldMovePlan(IReadOnlyList<PlannedMove> Moves, IReadOnlyDictionary<Guid, Enums.HoldMoveOutcome>? NotFound = null)
{
    /// <summary>A plan with no moves.</summary>
    public static readonly HoldMovePlan Empty = new([]);

    /// <summary>Gets a short, order-independent fingerprint of the moves that change anything (by outcome class, not by millimetres, so a re-run that measures a hold a few millimetres differently does not change it).</summary>
    public string Version
    {
        get
        {
            var text = string.Join(
                '\n',
                Moves.Where(m => m.Outcome != Enums.HoldMoveOutcome.Stayed)
                    .Select(m => $"{m.OldHoldId}>{m.NewHoldId}:{m.Outcome}")
                    .Concat((NotFound ?? new Dictionary<Guid, Enums.HoldMoveOutcome>()).Select(n => $"{n.Key}:{n.Value}"))
                    .Order(StringComparer.Ordinal));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 8);
        }
    }

    /// <summary>The move of an old hold, if one was measured.</summary>
    /// <param name="oldHoldId">The old hold.</param>
    /// <returns>The move or null.</returns>
    public PlannedMove? For(Guid oldHoldId) => Moves.FirstOrDefault(m => m.OldHoldId == oldHoldId);

    /// <summary>What the boulders of an old hold carried in place onto <paramref name="successorId"/> are told, if it is in <see cref="NotFound"/>.</summary>
    /// <param name="oldHoldId">The old hold.</param>
    /// <param name="successorId">Its successor (a copy at the predicted spot).</param>
    /// <returns>A zero-distance entry with the outcome, or null.</returns>
    public PlannedMove? NotFoundFor(Guid oldHoldId, Guid successorId) =>
        NotFound is not null && NotFound.TryGetValue(oldHoldId, out var outcome)
            ? new PlannedMove(oldHoldId, successorId, new HoldMoveMeasure(0, Enums.HoldMoveSource.TwoD, null), outcome)
            : null;
}
