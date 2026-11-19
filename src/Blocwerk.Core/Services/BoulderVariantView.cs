// <copyright file="BoulderVariantView.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Services;

/// <summary>One other published boulder of a boulder's family of variants.</summary>
/// <param name="Id">The boulder.</param>
/// <param name="Name">Its name.</param>
/// <param name="Grade">Its grade, if set.</param>
/// <param name="IsHistoric">True when the wall has changed since it was set.</param>
/// <param name="IsParent">True for the family's parent.</param>
public sealed record BoulderVariantView(Guid Id, string Name, string? Grade, bool IsHistoric, bool IsParent);
