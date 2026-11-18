// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Enums;
using Blocwerk.Core.Helpers;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Pages.Walls;

/// <summary>
/// The boulder list's style browser. Opening the list from the wall's Boulders card shows one card per style a boulder
/// carries (<c>BoulderStyleCards</c>), and a tap opens the list limited to it. The choice lives in the URL
/// (<c>?view=boulders&amp;style=tension</c>, <c>style=all</c> for the whole list), so reload and back behave: the cards
/// are the entry without a style, every card a history entry on top of it. A wall where no boulder has a style yet skips
/// the cards, so nothing gets worse before tagging starts.
/// </summary>
public partial class WallDetail
{
    private IReadOnlyDictionary<BoulderStyle, int>? styleCountsCache;
    private object? styleCountsItemsRef;

    /// <summary>Gets or sets the <c>?style=</c> query value: a style's name, <c>all</c>, or absent for the cards.</summary>
    [SupplyParameterFromQuery(Name = "style")]
    public string? Style { get; set; }

    /// <summary>The style the list is limited to, or null for every boulder.</summary>
    private BoulderStyle? StyleFilter => BoulderStyles.FromSlug(Style);

    /// <summary>
    /// The boulders the list shows as its rows (archived ones sit apart): the ONE query both the list and the style
    /// cards' counts start from, so a rule about what counts as a row (variant families, say) is applied here only.
    /// </summary>
    private IEnumerable<BoulderListItem> VisibleItems() => _items.Where(i => !i.Boulder.IsArchived);

    private int StyleTotal => VisibleItems().Count();

    // Boulders per style over what the list shows (archived ones apart), not over the filtered rows: a card's number is the wall's.
    private IReadOnlyDictionary<BoulderStyle, int> StyleCounts
    {
        get
        {
            if (styleCountsCache is null || !ReferenceEquals(styleCountsItemsRef, _items))
            {
                styleCountsItemsRef = _items;
                styleCountsCache = BoulderStyles.CountBy(VisibleItems().Select(i => i.Boulder.Styles));
            }

            return styleCountsCache;
        }
    }

    // The "+ New" button's rule, as the list head applies it.
    private bool ShowNewBoulderLink => _holds.Count > 0 && (!_isAnonymous || _canCreateAnonymously);

    private bool ShowStyleCards => string.IsNullOrEmpty(Style) && StyleCounts.Values.Any(c => c > 0);

    // The same wall URL with another style (null = all) or none (the cards), whatever else it carries (a share token sits in the path).
    private string StyleHref(BoulderStyle? style) => StyleUri(style is { } s ? BoulderStyles.Slug(s) : "all");

    private string StyleUri(string? slug) => Navigation.GetUriWithQueryParameters(
        new Dictionary<string, object?> { ["view"] = "boulders", ["style"] = slug });

    // The Boulders card on the overview: always the cards, even when a style is still in the URL from an earlier visit.
    private Task GoToBouldersFromOverviewAsync()
    {
        if (string.IsNullOrEmpty(Style))
        {
            return _carousel?.GoToPageAsync(3) ?? Task.CompletedTask;
        }

        Navigation.NavigateTo(StyleUri(null), replace: true);
        return Task.CompletedTask;
    }
}
