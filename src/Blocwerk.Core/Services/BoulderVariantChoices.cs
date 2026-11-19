// <copyright file="BoulderVariantChoices.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Services;

/// <summary>What the Mark Variant page offers for one boulder.</summary>
/// <param name="BoulderName">The boulder the page started from.</param>
/// <param name="CanEdit">Whether the viewer may change its variants.</param>
/// <param name="ParentId">The boulder's parent, when it is a variant.</param>
/// <param name="Choices">The wall's other published boulders, its own family first.</param>
/// <param name="UnlistedVariants">Its variants the page does not list (drafts, archived or historic); they stay linked.</param>
public sealed record BoulderVariantChoices(
    string BoulderName, bool CanEdit, Guid? ParentId, IReadOnlyList<BoulderVariantChoice> Choices, int UnlistedVariants);
