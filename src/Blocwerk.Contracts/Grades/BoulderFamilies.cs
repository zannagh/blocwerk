// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Helpers;

/// <summary>
/// How a boulder list folds families of variants, the same on the Server's list and in the offline client. A published
/// variant is hidden under its parent. A draft variant keeps its own row until it is published, or nobody could find it to
/// finish it, and never counts towards the family; an archived one stays in the archive, where it can be restored.
/// </summary>
public static class BoulderFamilies
{
    /// <summary>The rows of a list, by boulder. A boulder hidden under its parent has none.</summary>
    /// <param name="boulders">The boulders the viewer may see.</param>
    /// <returns>Each listed boulder's row.</returns>
    public static IReadOnlyDictionary<Guid, BoulderFamilyRow> Rows(IEnumerable<BoulderFamilyMember> boulders)
    {
        var members = boulders.ToList();
        var byId = members.ToDictionary(m => m.Id);
        var variants = members.Where(m => IsUnderParent(m, byId)).ToLookup(m => m.ParentId!.Value);
        return members
            .Where(m => !IsUnderParent(m, byId))
            .ToDictionary(m => m.Id, m => RowOf(m, variants[m.Id].ToList()));
    }

    /// <summary>The family's grade: "6A–7A" from lowest to highest by the grade scale, or one grade when all are equal.</summary>
    /// <param name="grades">The members' grades.</param>
    /// <returns>The range; null when no member has a grade.</returns>
    public static string? GradeRange(IEnumerable<string?> grades)
    {
        var all = grades.Where(g => !string.IsNullOrEmpty(g)).ToList();
        var scored = all.Where(g => GradeScoring.GetScore(g, false) > 0).OrderBy(g => GradeScoring.GetScore(g, false)).ToList();
        if (scored.Count == 0)
        {
            return all.FirstOrDefault();
        }

        var (lowest, highest) = (scored[0], scored[^1]);
        return GradeScoring.GetScore(lowest, false) == GradeScoring.GetScore(highest, false) ? lowest : $"{lowest}–{highest}";
    }

    private static bool IsUnderParent(BoulderFamilyMember member, Dictionary<Guid, BoulderFamilyMember> byId) =>
        member is { ParentId: { } parentId, IsDraft: false, IsArchived: false }
        && byId.TryGetValue(parentId, out var parent)
        && parent is { IsDraft: false, IsArchived: false };

    private static BoulderFamilyRow RowOf(BoulderFamilyMember head, List<BoulderFamilyMember> variants)
    {
        var grades = variants.Select(v => v.Grade).Prepend(head.Grade).ToList();
        var scores = grades.Select(g => GradeScoring.GetScore(g, false)).Where(s => s > 0).ToList();
        return new BoulderFamilyRow(
            variants.Count,
            GradeRange(grades),
            scores.Count > 0 ? scores.Min() : 0,
            grades,
            variants.Select(v => v.Name).ToList(),
            variants.Aggregate(head.Styles, (styles, v) => styles | v.Styles));
    }
}
