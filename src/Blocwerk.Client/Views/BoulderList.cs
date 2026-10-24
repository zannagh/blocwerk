// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client.Store;
using Blocwerk.Contracts.Documents;
using Blocwerk.Core.Helpers;

namespace Blocwerk.Client.Views;

/// <summary>
/// The wall view's boulder list over the cached boulders document, by the Server's rules (WallDetail): filter, sort,
/// historic boulders sunk to the bottom of the active list, the archive apart. A grade range keeps the ungraded.
/// </summary>
public static class BoulderList
{
    /// <summary>The active and the archived rows, filtered and sorted.</summary>
    /// <param name="boulders">The boulders the class may see.</param>
    /// <param name="mine">The viewer's marks.</param>
    /// <param name="filter">The filter.</param>
    /// <returns>Both lists.</returns>
    public static (IReadOnlyList<BouldersDocumentBoulder> Active, IReadOnlyList<BouldersDocumentBoulder> Archived) Apply(
        IReadOnlyList<BouldersDocumentBoulder> boulders, MineMarks mine, BoulderFilter filter)
    {
        var active = Sort(Filter(boulders.Where(b => !b.IsArchived), mine, filter), mine, filter.Sort)
            .OrderBy(b => b.IsHistoric)
            .ToList();
        var archived = Sort(Filter(boulders.Where(b => b.IsArchived), mine, filter), mine, filter.Sort).ToList();
        return (active, archived);
    }

    private static IEnumerable<BouldersDocumentBoulder> Filter(IEnumerable<BouldersDocumentBoulder> src, MineMarks mine, BoulderFilter filter)
    {
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            src = src.Where(b => b.Name?.Contains(filter.Search, StringComparison.OrdinalIgnoreCase) == true);
        }

        if (filter.Attempted)
        {
            src = src.Where(b => mine.TickFor(b.Id) is not null);
        }

        if (filter.Done)
        {
            src = src.Where(b => mine.IsSent(b.Id));
        }

        if (filter.NotDone)
        {
            src = src.Where(b => !mine.IsSent(b.Id));
        }

        if (filter.Favourites)
        {
            src = src.Where(b => mine.IsFavourite(b.Id));
        }

        var min = ScoreOf(filter.GradeMin);
        var max = ScoreOf(filter.GradeMax);
        return min is null && max is null ? src : src.Where(b => InRange(b.Grade, min, max));
    }

    private static IEnumerable<BouldersDocumentBoulder> Sort(IEnumerable<BouldersDocumentBoulder> src, MineMarks mine, BoulderSort sort) => sort switch
    {
        BoulderSort.Oldest => src.OrderBy(b => b.CreatedAt),
        BoulderSort.GradeAsc => src.OrderBy(b => GradeScoring.GetScore(b.Grade, false) is 0 ? int.MaxValue : GradeScoring.GetScore(b.Grade, false)),
        BoulderSort.GradeDesc => src.OrderByDescending(b => GradeScoring.GetScore(b.Grade, false)),
        BoulderSort.Rating => src.OrderByDescending(b => b.Stats.RatingAverage ?? -1),
        BoulderSort.MyAttempts => src.OrderByDescending(b => mine.TickFor(b.Id)?.Attempts ?? 0),
        _ => src.OrderByDescending(b => b.CreatedAt),
    };

    // Ungraded (or unrecognised) boulders stay visible when a grade range is set.
    private static bool InRange(string? grade, int? min, int? max)
    {
        var score = GradeScoring.GetScore(grade, false);
        return score == 0 || ((min is null || score >= min) && (max is null || score <= max));
    }

    private static int? ScoreOf(string? grade)
    {
        var score = string.IsNullOrEmpty(grade) ? 0 : GradeScoring.GetScore(grade, false);
        return score == 0 ? null : score;
    }
}
