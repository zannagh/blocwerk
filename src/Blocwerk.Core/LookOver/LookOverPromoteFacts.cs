// <copyright file="LookOverPromoteFacts.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.LookOver;

/// <summary>What a promote knows when it is about to commit, as far as the look-over needs it.</summary>
/// <param name="Cards">The confirm screen's cards of the promoted session.</param>
/// <param name="Carry">The promoted carry decision per old hold.</param>
/// <param name="Successors">Old hold to the live hold it became.</param>
/// <param name="LiveHolds">Every hold of the new generation, after the promote.</param>
/// <param name="StagedHolds">Every staged hold of the re-photographed panels, whether it went live or was left out.</param>
/// <param name="NotFound">Old holds kept in place whose boulders the promote marked (not found again, or replaced).</param>
/// <param name="Boulders">Live hold to the number of current boulders the promote moved onto it.</param>
/// <param name="PhotoSizes">The new photos' pixel sizes per panel.</param>
/// <param name="CardMarked">
/// Old holds whose successor the promote marked for review because of an unanswered card, and was not marked before
/// (<see cref="LookOverPromoteInputs.CardMarked"/>).
/// </param>
/// <param name="UnalignedPanelIds">Panels whose holds the promote marks anyway (their photo could not be lined up).</param>
/// <param name="NewlyMarked">
/// Live hold to the boulders the promote marked BECAUSE that hold was not found again: not marked before, told
/// "not found again" about this hold, and given no other reason by the same promote.
/// </param>
internal sealed record LookOverPromoteFacts(
    IReadOnlyList<WallUpdateException> Cards,
    IReadOnlyDictionary<Guid, CarryoverDecision> Carry,
    IReadOnlyDictionary<Guid, Guid> Successors,
    IReadOnlyDictionary<Guid, Hold> LiveHolds,
    IReadOnlyDictionary<Guid, Hold> StagedHolds,
    IReadOnlyDictionary<Guid, HoldMoveOutcome> NotFound,
    IReadOnlyDictionary<Guid, int> Boulders,
    IReadOnlyDictionary<Guid, (int Width, int Height)> PhotoSizes,
    IReadOnlySet<Guid> CardMarked,
    IReadOnlySet<Guid> UnalignedPanelIds,
    IReadOnlyDictionary<Guid, List<Guid>> NewlyMarked);
