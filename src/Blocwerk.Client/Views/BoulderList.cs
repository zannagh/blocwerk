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
    /// <param name="holds">The holds document, for the boulders' hold types; null when the device does not hold it.</param>
    /// <returns>Both lists.</returns>
    public static (IReadOnlyList<BouldersDocumentBoulder> Active, IReadOnlyList<BouldersDocumentBoulder> Archived) Apply(
        IReadOnlyList<BouldersDocumentBoulder> boulders, MineMarks mine, BoulderFilter filter, HoldsDocument? holds = null)
    {
        var families = Families(boulders, holds);
        var active = Sort(Filter(Visible(boulders), mine, filter, families), mine, filter.Sort, families)
            .OrderBy(b => b.IsHistoric)
            .ToList();
        var archived = Sort(Filter(boulders.Where(b => b.IsArchived && families.ContainsKey(b.Id)), mine, filter, families), mine, filter.Sort, families).ToList();
        return (active, archived);
    }

    /// <summary>
    /// The boulders the list shows as rows (archived ones sit apart, a variant under its parent has no row of its own):
    /// the ONE query the list and the style cards' counts both start from, so a rule about what counts as a row is applied
    /// here only.
    /// </summary>
    /// <param name="boulders">The boulders the class may see.</param>
    /// <returns>The active ones.</returns>
    public static IReadOnlyList<BouldersDocumentBoulder> Visible(IReadOnlyList<BouldersDocumentBoulder> boulders)
    {
        var families = Families(boulders);
        return boulders.Where(b => !b.IsArchived && families.ContainsKey(b.Id)).ToList();
    }

    /// <summary>
    /// How many boulders the wall has on the wall page's overview: every active boulder, a variant as much as a parent
    /// (unlike <see cref="Visible"/>, which is the list's rows and counts a family once).
    /// </summary>
    /// <param name="boulders">The boulders the class may see.</param>
    /// <returns>The count of non-archived boulders.</returns>
    public static int ActiveCount(IReadOnlyList<BouldersDocumentBoulder> boulders) => boulders.Count(b => !b.IsArchived);

    /// <summary>Families per style over <see cref="Visible"/>, as the cards show them: a family counts once, under every style a published member has.</summary>
    /// <param name="boulders">The boulders the class may see.</param>
    /// <returns>The count per style.</returns>
    public static IReadOnlyDictionary<Blocwerk.Core.Enums.BoulderStyle, int> StyleCounts(IReadOnlyList<BouldersDocumentBoulder> boulders)
    {
        var families = Families(boulders);
        return BoulderStyles.CountBy(Visible(boulders).Select(b => families[b.Id].Styles));
    }

    /// <summary>Families on each of the viewer's own lists over the list's rows, as their landing cards show them.</summary>
    /// <param name="boulders">The boulders the class may see.</param>
    /// <param name="mine">The viewer's marks.</param>
    /// <param name="families">The rows by boulder (<see cref="Families"/>), already built for the list.</param>
    /// <returns>How many rows each list has.</returns>
    public static IReadOnlyDictionary<BoulderMine, int> MineCounts(
        IReadOnlyList<BouldersDocumentBoulder> boulders, MineMarks mine, IReadOnlyDictionary<Guid, BoulderFamilyRow> families)
    {
        var rows = boulders.Where(b => !b.IsArchived && families.ContainsKey(b.Id)).ToList();
        return new Dictionary<BoulderMine, int>
        {
            [BoulderMine.Projects] = rows.Count(b => AnyMember(b, families, mine.IsProject)),
            [BoulderMine.Favorites] = rows.Count(b => AnyMember(b, families, mine.IsFavourite)),
        };
    }

    /// <summary>The rows of the list by boulder: a variant under a listed parent has none.</summary>
    /// <param name="boulders">The boulders the class may see.</param>
    /// <param name="holds">The holds document, for the boulders' hold types; null when the device does not hold it.</param>
    /// <returns>Each listed boulder's family.</returns>
    public static IReadOnlyDictionary<Guid, BoulderFamilyRow> Families(IReadOnlyList<BouldersDocumentBoulder> boulders, HoldsDocument? holds = null)
    {
        var byId = DocumentHoldTypes.Index(holds);
        return BoulderFamilies.Rows(boulders.Select(b => new BoulderFamilyMember(
            b.Id, b.ParentBoulderId, b.Name, b.Grade, b.Draft == true, b.IsArchived, BoulderStyles.FromNames(b.Styles), b.IsHistoric,
            DocumentHoldTypes.Of(b, byId)?.Type)));
    }

    private static IEnumerable<BouldersDocumentBoulder> Filter(
        IEnumerable<BouldersDocumentBoulder> src, MineMarks mine, BoulderFilter filter, IReadOnlyDictionary<Guid, BoulderFamilyRow> families)
    {
        if (filter.Style is { } style)
        {
            src = src.Where(b => families[b.Id].Styles.HasFlag(style));
        }

        if (filter.HoldType is { } hold)
        {
            src = src.Where(b => families[b.Id].HoldTypes?.Contains(hold) == true);
        }

        if (filter.Mine is BoulderMine.Favorites)
        {
            src = src.Where(b => AnyMember(b, families, mine.IsFavourite));
        }

        if (filter.Mine is BoulderMine.Projects)
        {
            src = src.Where(b => AnyMember(b, families, mine.IsProject));
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            src = src.Where(b => b.Name?.Contains(filter.Search, StringComparison.OrdinalIgnoreCase) == true
                || families[b.Id].VariantNames.Any(name => name.Contains(filter.Search, StringComparison.OrdinalIgnoreCase)));
        }

        if (filter.Attempted)
        {
            src = src.Where(b => AnyMember(b, families, id => mine.TickFor(id) is not null));
        }

        if (filter.Done)
        {
            src = src.Where(b => AnyMember(b, families, mine.IsSent));
        }

        if (filter.NotDone)
        {
            src = src.Where(b => AnyMember(b, families, id => !mine.IsSent(id)));
        }

        if (filter.Favourites)
        {
            src = src.Where(b => AnyMember(b, families, mine.IsFavourite));
        }

        var min = ScoreOf(filter.GradeMin);
        var max = ScoreOf(filter.GradeMax);
        return min is null && max is null ? src : src.Where(b => BoulderFamilies.MatchesGradeRange(families[b.Id], min, max));
    }

    // The viewer's marks keep a family in a filter when any of its published members has them.
    private static bool AnyMember(BouldersDocumentBoulder b, IReadOnlyDictionary<Guid, BoulderFamilyRow> families, Func<Guid, bool> test) =>
        test(b.Id) || (families[b.Id].VariantIds ?? []).Any(test);

    private static IEnumerable<BouldersDocumentBoulder> Sort(
        IEnumerable<BouldersDocumentBoulder> src, MineMarks mine, BoulderSort sort, IReadOnlyDictionary<Guid, BoulderFamilyRow> families) => sort switch
    {
        BoulderSort.Oldest => src.OrderBy(b => b.CreatedAt),
        BoulderSort.GradeAsc => src.OrderBy(b => families[b.Id].LowestScore is 0 ? int.MaxValue : families[b.Id].LowestScore),
        BoulderSort.GradeDesc => src.OrderByDescending(b => families[b.Id].LowestScore),
        BoulderSort.Rating => src.OrderByDescending(b => b.Stats.RatingAverage ?? -1),
        BoulderSort.MyAttempts => src.OrderByDescending(b => mine.TickFor(b.Id)?.Attempts ?? 0),
        _ => src.OrderByDescending(b => b.CreatedAt),
    };

    private static int? ScoreOf(string? grade)
    {
        var score = string.IsNullOrEmpty(grade) ? 0 : GradeScoring.GetScore(grade, false);
        return score == 0 ? null : score;
    }
}
