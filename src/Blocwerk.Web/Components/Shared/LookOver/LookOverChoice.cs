// <copyright file="LookOverChoice.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;

namespace Blocwerk.Web.Components.Shared.LookOver;

/// <summary>An answer picked on a look-over card, with the boulders the card showed when it was picked.</summary>
/// <param name="FindingId">The finding.</param>
/// <param name="Answer">The answer.</param>
/// <param name="ShownBoulderIds">The boulders listed on the card (and named in its confirm question).</param>
public sealed record LookOverChoice(Guid FindingId, LookOverAnswer Answer, IReadOnlyCollection<Guid> ShownBoulderIds);
