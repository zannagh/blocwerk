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
public sealed record HoldMovePlan(IReadOnlyList<PlannedMove> Moves)
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
                    .Order(StringComparer.Ordinal));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 8);
        }
    }

    /// <summary>The move of an old hold, if one was measured.</summary>
    /// <param name="oldHoldId">The old hold.</param>
    /// <returns>The move or null.</returns>
    public PlannedMove? For(Guid oldHoldId) => Moves.FirstOrDefault(m => m.OldHoldId == oldHoldId);
}
