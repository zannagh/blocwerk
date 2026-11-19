// <copyright file="BoulderVariantChoice.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Services;

/// <summary>One boulder of the wall the Mark Variant page offers.</summary>
/// <param name="Id">The boulder.</param>
/// <param name="Name">Its name.</param>
/// <param name="Grade">Its grade, if set.</param>
/// <param name="IsVariant">True when it is a variant of the boulder the page started from.</param>
/// <param name="IsParent">True when it is the parent of the boulder the page started from.</param>
/// <param name="IsSibling">True when it is a variant of the same parent as the boulder the page started from.</param>
/// <param name="CanMove">True when the viewer may make it a variant here: they may edit it, and the family it leaves.</param>
/// <param name="VariantCount">How many variants it has, which move along with it.</param>
/// <param name="ParentName">The parent it is a variant of now, if any.</param>
public sealed record BoulderVariantChoice(Guid Id, string Name, string? Grade, bool IsVariant, bool IsParent, bool IsSibling, bool CanMove, int VariantCount, string? ParentName);
