// <copyright file="LookOverPromoteInputs.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.HoldMoves;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.LookOver;

/// <summary>What <see cref="WallBigUpdateService.PromoteAsync"/> hands to its look-over step.</summary>
/// <param name="Confirmation">The promoted decisions (accepted relocations folded in).</param>
/// <param name="Moves">The move plan the promote acted on.</param>
/// <param name="StagedHolds">Every staged hold of the re-photographed panels.</param>
/// <param name="ReplacedPanelIds">The live panels the update replaces.</param>
/// <param name="PhotoSizes">The new photos' pixel sizes per staged panel.</param>
internal sealed record LookOverPromoteInputs(
    BigUpdateConfirmation Confirmation,
    HoldMovePlan Moves,
    IReadOnlyDictionary<Guid, Hold> StagedHolds,
    IReadOnlyCollection<Guid> ReplacedPanelIds,
    IReadOnlyDictionary<Guid, (int Width, int Height)> PhotoSizes)
{
    /// <summary>
    /// Gets the old holds whose successor the carry marked for review because a card about them was not answered, and
    /// only those where the successor was unmarked until then. Filled by the carry: whether another reason had already
    /// marked the hold is only known at that moment.
    /// </summary>
    public HashSet<Guid> CardMarked { get; } = [];
}
