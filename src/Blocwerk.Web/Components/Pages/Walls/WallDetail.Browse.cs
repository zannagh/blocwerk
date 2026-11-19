// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Enums;
using Blocwerk.Core.Helpers;
using Blocwerk.Core.Services;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Pages.Walls;

/// <summary>
/// The boulder list's browser. Opening the list from the wall's Boulders card shows three cards, All, By Hold Type and
/// By Style (<c>BoulderBrowser</c>); the latter two open a page of cards, and a card opens the list limited to it. Every
/// level lives in the URL (<see cref="BoulderBrowse"/>: <c>?view=boulders&amp;browse=holds</c>, <c>&amp;hold=crimp</c>,
/// <c>&amp;style=power</c>, <c>&amp;style=all</c>, <c>&amp;mine=favorites</c>), so reload and back return to it. A
/// viewer with own lists also gets a card per list (WallDetail.OwnLists.cs). On the desktop tier the landing renders the
/// whole list under the cards (WallDetail.BrowseTier.cs).
/// </summary>
public partial class WallDetail
{
    /// <summary>Gets or sets the <c>?browse=</c> query value: <c>holds</c> or <c>styles</c> for a card page.</summary>
    [SupplyParameterFromQuery(Name = BoulderBrowse.BrowseKey)]
    public string? Browse { get; set; }

    /// <summary>Gets or sets the <c>?style=</c> query value: a style's name or <c>all</c>.</summary>
    [SupplyParameterFromQuery(Name = BoulderBrowse.StyleKey)]
    public string? Style { get; set; }

    /// <summary>Gets or sets the <c>?hold=</c> query value: a hold type's name.</summary>
    [SupplyParameterFromQuery(Name = BoulderBrowse.HoldKey)]
    public string? Hold { get; set; }

    /// <summary>Gets or sets the <c>?mine=</c> query value: one of the viewer's own lists (<c>projects</c>, <c>favorites</c>).</summary>
    [SupplyParameterFromQuery(Name = BoulderBrowse.MineKey)]
    public string? Mine { get; set; }

    /// <summary>The browser's level as the URL names it; <c>?mine=</c> only counts for a viewer with own lists.</summary>
    private BoulderBrowse Browsing => BoulderBrowse.From(Browse, Style, Hold, HasOwnLists ? Mine : null);

    /// <summary>
    /// The boulders the list shows as its rows (archived ones sit apart): the ONE query both the list and the browser's
    /// counts start from, so a rule about what counts as a row (variant families, say) is applied here only.
    /// </summary>
    private IEnumerable<BoulderListItem> VisibleItems() => _items.Where(i => !i.Boulder.IsArchived && FamilyRows.ContainsKey(i.Boulder.Id));

    // The families the browser's cards count: the wall's, not the filtered rows.
    private IReadOnlyCollection<BoulderFamilyRow> BrowseRows => VisibleItems().Select(FamilyOf).ToList();

    // The "+ New" button's rule, as the list head applies it.
    private bool ShowNewBoulderLink => _holds.Count > 0 && (!_isAnonymous || _canCreateAnonymously);

    private bool ShowBrowseCards => Browsing.Level != BoulderBrowseLevel.List;

    // A list level shows its list; the landing carries the whole list under its cards only on the desktop tier, and
    // never on a kiosk; the card pages carry none.
    private bool ShowBoulderList => Browsing.Level == BoulderBrowseLevel.List
        || (Browsing.Level == BoulderBrowseLevel.Landing && desktopTier && !KioskContext.IsKiosk);

    // The same wall URL at another level of the browser, whatever else it carries (a share token sits in the path).
    private string BrowseHref(BoulderBrowse level)
    {
        var query = level.Query();
        query["view"] = "boulders";
        return Navigation.GetUriWithQueryParameters(query);
    }

    // The Boulders card on the overview: always the landing, even when a level is still in the URL from an earlier visit.
    private Task GoToBouldersFromOverviewAsync()
    {
        if (Browsing.Level == BoulderBrowseLevel.Landing)
        {
            return _carousel?.GoToPageAsync(3) ?? Task.CompletedTask;
        }

        Navigation.NavigateTo(BrowseHref(BoulderBrowse.Landing), replace: true);
        return Task.CompletedTask;
    }

    // Whether a row stays in the list the URL names: a family shows under every style and hold type a published member has.
    private bool MatchesBrowse(BoulderListItem item) => Browsing switch
    {
        { Style: { } style } => FamilyOf(item).Styles.HasFlag(style),
        { HoldType: { } hold } => FamilyOf(item).HoldTypes.HasFlag(hold),
        { Mine: BoulderMine.Favorites } => AnyMember(item, m => m.IsFavorite),
        { Mine: BoulderMine.Projects } => AnyMember(item, m => m.IsProject),
        _ => true,
    };
}
