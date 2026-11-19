// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Helpers;

/// <summary>What the boulder lists need to know of one boulder to group it into its family.</summary>
/// <param name="Id">The boulder.</param>
/// <param name="ParentId">The boulder it is a variant of, if any.</param>
/// <param name="Name">Its name.</param>
/// <param name="Grade">Its grade, if set.</param>
/// <param name="IsDraft">True for a draft.</param>
/// <param name="IsArchived">True for an archived boulder.</param>
/// <param name="Styles">Its style tags.</param>
/// <param name="IsHistoric">True for a boulder the wall has moved on from.</param>
public readonly record struct BoulderFamilyMember(Guid Id, Guid? ParentId, string Name, string? Grade, bool IsDraft, bool IsArchived, BoulderStyle Styles = BoulderStyle.None, bool IsHistoric = false);
