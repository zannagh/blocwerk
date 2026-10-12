// <copyright file="LinkedHoldColorRule.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Holds.Colors;

/// <summary>
/// One colour for a hold that several panels show (linked holds): every view is estimated on its own photo and the
/// group gets a single answer. Views that agree, or a single sure view next to unsure ones, give that colour. When two
/// sure views name different colours, the larger view wins only when it is clearly larger; otherwise the hold stays
/// without a colour.
/// </summary>
public static class LinkedHoldColorRule
{
    /// <summary>How many times larger the winning view must be than a view that says another colour.</summary>
    public const double ClearlyLarger = 2.0;

    /// <summary>Chooses the group's colour.</summary>
    /// <param name="views">The estimate of each view of the hold.</param>
    /// <returns>The estimate that stands for the group.</returns>
    public static HoldColorEstimate Choose(IReadOnlyList<HoldColorEstimate> views)
    {
        var largest = views.MaxBy(v => v.Area) ?? HoldColorEstimate.Unset(HoldColorVerdict.TooFewPixels);
        var best = views.Where(v => v.Color is not null).MaxBy(v => v.Area);
        if (best is null)
        {
            return HoldColorEstimate.Unset(largest.Verdict);
        }

        var rival = views.Where(v => v.Color is not null && v.Color != best.Color).MaxBy(v => v.Area);
        return rival is null || best.Area >= ClearlyLarger * rival.Area
            ? best
            : HoldColorEstimate.Unset(HoldColorVerdict.ViewsDisagree);
    }
}
