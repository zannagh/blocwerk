// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Helpers;

/// <summary>One row of a boulder list: a boulder and the published variants it stands for.</summary>
/// <param name="VariantCount">How many published variants the row stands for.</param>
/// <param name="GradeRange">The family's grade, lowest to highest; one grade when all are equal; null when none is set.</param>
/// <param name="LowestScore">The score of the family's lowest grade; 0 when none is graded.</param>
/// <param name="Grades">The grade of every published member, the row's own included.</param>
/// <param name="VariantNames">The names of the variants, which the list's search also finds the row by.</param>
/// <param name="VariantIds">The variants the row stands for, so a filter can match the family through any of them.</param>
/// <param name="Styles">Every style tag a published member carries: the family shows under each of them.</param>
/// <param name="HoldTypes">Every effective hold type a published member has: the family shows under each of them.</param>
public sealed record BoulderFamilyRow(
    int VariantCount,
    string? GradeRange,
    int LowestScore,
    IReadOnlyList<string?> Grades,
    IReadOnlyList<string> VariantNames,
    BoulderStyle Styles = BoulderStyle.None,
    IReadOnlyList<Guid>? VariantIds = null,
    IReadOnlyList<HoldHandType>? HoldTypes = null);
