// <copyright file="CarryConfirmation.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Services;

/// <summary>
/// Who confirmed one old hold's carry verdict, and when. The attribution half of the review state, kept
/// out of <see cref="BigUpdateConfirmation"/> on purpose: that record is the promote's INPUT, and review
/// metadata has no business travelling into <see cref="IWallBigUpdateService.PromoteAsync"/>.
/// </summary>
/// <param name="OldHoldId">The old live hold whose verdict was confirmed.</param>
/// <param name="ConfirmedByUserId">
/// The wall admin who confirmed it. Null only for a confirmation recorded without a resolvable user.
/// </param>
/// <param name="ConfirmedByName">
/// That admin's effective display name, or null when the user id is null or the account no longer reads.
/// </param>
/// <param name="ConfirmedAt">When the confirmation was recorded.</param>
public record CarryConfirmation(
    Guid OldHoldId,
    Guid? ConfirmedByUserId,
    string? ConfirmedByName,
    DateTimeOffset ConfirmedAt);
