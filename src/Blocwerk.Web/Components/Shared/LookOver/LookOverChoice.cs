// <copyright file="LookOverChoice.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;

namespace Blocwerk.Web.Components.Shared.LookOver;

/// <summary>An answer picked on a look-over card, with the boulders the card showed when it was picked.</summary>
/// <param name="FindingId">The finding.</param>
/// <param name="Answer">The answer.</param>
/// <param name="ShownBoulderIds">The boulders listed on the card (and named in its confirm question).</param>
/// <param name="NewHoldBoulderIds">
/// With "use the new hold": the boulders answered yes to "Should this boulder use the new hold in that place?"; null
/// when every boulder said yes.
/// </param>
public sealed record LookOverChoice(
    Guid FindingId, LookOverAnswer Answer, IReadOnlyCollection<Guid> ShownBoulderIds, IReadOnlyCollection<Guid>? NewHoldBoulderIds = null);
