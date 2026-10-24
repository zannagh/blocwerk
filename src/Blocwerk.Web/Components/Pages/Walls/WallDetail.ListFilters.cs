// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Blocwerk.Web.Components.Pages.Walls;

/// <summary>
/// The boulder list's search field and its remembered filters.
/// <para>
/// Search: a lens next to the Filters chip opens a field that filters the list as you type (<see cref="BoulderListSearch"/>).
/// It sits in the sticky head, so it is there wherever the list is scrolled, and it is the way to look a boulder up:
/// the browser's find-in-page sees every carousel page at once. Closing it (the close button or Escape) clears it.
/// </para>
/// <para>
/// Filters: remembered per wall in <see cref="BoulderListFilterCookie"/>. The prerender pass shows "Loading..." and no
/// list, so reading the cookie in the interactive pass before the wall loads is what keeps the first list render
/// filtered. Not on a share link or a kiosk tablet: the one is someone else's view, the other a shared device whose next
/// climber should not inherit the last one's filters.
/// </para>
/// </summary>
public partial class WallDetail
{
    private const string SearchRowId = "boulder-search-row";

    private bool _searchOpen;
    private bool _focusSearch;
    private ElementReference _searchToggle;
    private ElementReference _searchInput;

    // The cookie as last read or written, and the filters it holds for this wall: a render whose filters differ writes.
    private string? _filterCookie;
    private WallListFilter? _savedFilter;

    private bool PersistsListFilters => string.IsNullOrEmpty(ShareToken) && !KioskContext.IsKiosk;

    private WallListFilter CurrentListFilter() => new(
        _searchText, _gradeMin, _gradeMax, _fAttempted, _fDone, _fNotDone, _fFavorites, _fCreated, _fBeta, _fNeedsReview, (int)_sort);

    private void ApplyListFilter(WallListFilter filter)
    {
        _searchText = filter.SearchText;
        _gradeMin = filter.GradeMin;
        _gradeMax = filter.GradeMax;
        _fAttempted = filter.Attempted;
        _fDone = filter.Done;
        _fNotDone = filter.NotDone;
        _fFavorites = filter.Favorites;
        _fCreated = filter.Created;
        _fBeta = filter.Beta;
        _fNeedsReview = filter.NeedsReview;
        _sort = Enum.IsDefined((SortMode)filter.Sort) ? (SortMode)filter.Sort : SortMode.Newest;
    }

    // Before the wall loads. A page restored from memory keeps the filters it was left with (they are this wall's
    // anyway); a fresh page, or one switched to another wall, takes the wall's stored filters, or the defaults.
    private async Task LoadListFiltersAsync(bool restored)
    {
        _searchOpen = restored && !string.IsNullOrEmpty(_searchText);
        _savedFilter = null;
        if (!PersistsListFilters)
        {
            return;
        }

        try
        {
            _filterCookie = await JS.InvokeAsync<string?>("bwPrefs.getBoulderFilters");
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or TaskCanceledException or InvalidOperationException)
        {
            // Preferences only: without them the list opens unfiltered, as it always did.
            Logger.LogDebug(ex, "Could not read the boulder list filters of wall {WallId}", WallId);
            return;
        }

        if (!restored)
        {
            ApplyListFilter(BoulderListFilterCookie.Read(_filterCookie, WallId) ?? DefaultListFilter);
        }

        _savedFilter = BoulderListFilterCookie.Normalize(CurrentListFilter());
    }

    private static WallListFilter DefaultListFilter { get; } =
        new(string.Empty, null, null, false, false, false, false, false, false, false, (int)SortMode.Newest);

    // Every render: a filter or the sort changed since the cookie was written → write it. Nothing to do otherwise.
    private async Task SaveListFiltersAsync()
    {
        if (_savedFilter is null || _loadedWallId == Guid.Empty || !PersistsListFilters)
        {
            return;
        }

        var current = BoulderListFilterCookie.Normalize(CurrentListFilter());
        if (current == _savedFilter)
        {
            return;
        }

        _savedFilter = current;
        _filterCookie = BoulderListFilterCookie.Write(_filterCookie, _loadedWallId, current);
        try
        {
            await JS.InvokeVoidAsync("bwPrefs.setBoulderFilters", _filterCookie);
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or TaskCanceledException or InvalidOperationException)
        {
            Logger.LogDebug(ex, "Could not store the boulder list filters of wall {WallId}", _loadedWallId);
        }
    }

    // list-search.js has already shown the row and focused the field inside the tap, which is what raises a phone's
    // keyboard (a focus that arrives after the round trip does not); the focus below is for everything else.
    private void ToggleSearch()
    {
        if (_searchOpen)
        {
            CloseSearch();
            return;
        }

        _searchOpen = true;
        _focusSearch = true;
    }

    private void CloseSearch()
    {
        _searchOpen = false;
        _searchText = string.Empty;
    }

    // Escape closes the search. Element-scoped rather than through bwKeys: the dispatcher stands down on typing
    // targets, which is exactly where focus is while searching.
    private async Task OnSearchKeyDownAsync(KeyboardEventArgs e)
    {
        if (!_searchOpen || e.Key != "Escape")
        {
            return;
        }

        CloseSearch();
        await _searchToggle.FocusAsync();
    }

    private async Task AfterListRenderAsync()
    {
        await SaveListFiltersAsync();
        if (!_focusSearch || !_searchOpen)
        {
            return;
        }

        _focusSearch = false;
        try
        {
            await _searchInput.FocusAsync();
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or TaskCanceledException or InvalidOperationException)
        {
            Logger.LogDebug(ex, "Could not focus the boulder search");
        }
    }
}
