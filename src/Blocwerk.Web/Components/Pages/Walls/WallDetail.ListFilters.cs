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
/// the browser's find-in-page only sees the carousel page on screen. Closing it (the close button or Escape) clears it,
/// and so does moving to another wall.
/// </para>
/// <para>
/// Filters: remembered per user and wall in <see cref="BoulderListFilterCookie"/>. The prerender pass shows "Loading..."
/// and no list, so the cookie is read in the interactive pass alongside the wall load and applied before the render that
/// first shows the list. Not on a share link or a kiosk tablet: the one is someone else's view, the other a shared device
/// whose next climber should not inherit the last one's filters.
/// </para>
/// </summary>
public partial class WallDetail
{
    private const string SearchRowId = "boulder-search-row";

    private bool searchOpen;
    private bool focusSearch;
    private ElementReference searchToggle;
    private ElementReference searchInput;

    // The filters the cookie holds for this user and wall, as last read or written: a render whose filters differ
    // writes. Null until the first read has finished (nothing is written before it).
    private WallListFilter? savedFilter;

    private bool PersistsListFilters => string.IsNullOrEmpty(ShareToken) && !KioskContext.IsKiosk;

    private static WallListFilter DefaultListFilter { get; } =
        new(string.Empty, null, null, false, false, false, false, false, false, false, (int)SortMode.Newest);

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

    // Loads the wall with the cookie read running beside it. The user is only known once the wall has loaded, and
    // nothing renders in between, so the stored filters are on before the list is. A page restored from memory keeps
    // the filters it was left with (they are this wall's anyway); a fresh page or another wall takes the stored ones,
    // or the defaults.
    private async Task LoadWallAndListFiltersAsync(bool restored)
    {
        savedFilter = null;
        var cookie = PersistsListFilters ? ReadFilterCookieAsync() : Task.FromResult<string?>(null);
        await LoadCurrentWall();
        var stored = await cookie;
        if (!PersistsListFilters || _currentUserId == Guid.Empty)
        {
            return;
        }

        if (!restored)
        {
            var filter = BoulderListFilterCookie.Read(stored, _currentUserId, WallId) ?? DefaultListFilter;
            ApplyListFilter(filter with { SearchText = _searchText });
        }

        savedFilter = BoulderListFilterCookie.Normalize(CurrentListFilter());
    }

    // A cookie that cannot be read (a failed round trip; prefs.js already turns a malformed value into "unset") counts
    // as an empty one: the list opens unfiltered and the next save replaces whatever is there.
    private async Task<string?> ReadFilterCookieAsync()
    {
        try
        {
            return await JS.InvokeAsync<string?>("bwPrefs.getBoulderFilters");
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or TaskCanceledException or InvalidOperationException)
        {
            Logger.LogDebug(ex, "Could not read the boulder list filters of wall {WallId}", WallId);
            return null;
        }
    }

    // Every render: a filter or the sort changed since the cookie was written → write it. The cookie is read again
    // first, so what another tab stored for other walls since this one loaded is merged into, not overwritten.
    private async Task SaveListFiltersAsync()
    {
        if (savedFilter is null || _loadedWallId == Guid.Empty || _currentUserId == Guid.Empty || !PersistsListFilters)
        {
            return;
        }

        var current = BoulderListFilterCookie.Normalize(CurrentListFilter());
        if (current == savedFilter)
        {
            return;
        }

        savedFilter = current;
        var (userId, wallId) = (_currentUserId, _loadedWallId);
        var latest = await ReadFilterCookieAsync();
        try
        {
            await JS.InvokeVoidAsync("bwPrefs.setBoulderFilters", BoulderListFilterCookie.Write(latest, userId, wallId, current));
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or TaskCanceledException or InvalidOperationException)
        {
            Logger.LogDebug(ex, "Could not store the boulder list filters of wall {WallId}", wallId);
        }
    }

    // list-search.js has already shown the row and focused the field inside the tap, which is what raises a phone's
    // keyboard (a focus that arrives after the round trip does not); the focus below is for everything else.
    private void ToggleSearch()
    {
        if (searchOpen)
        {
            CloseSearch();
            return;
        }

        searchOpen = true;
        focusSearch = true;
    }

    private void CloseSearch()
    {
        searchOpen = false;
        _searchText = string.Empty;
    }

    // Escape closes the search. Element-scoped rather than through bwKeys: the dispatcher stands down on typing
    // targets, which is exactly where focus is while searching.
    private async Task OnSearchKeyDownAsync(KeyboardEventArgs e)
    {
        if (!searchOpen || e.Key != "Escape")
        {
            return;
        }

        CloseSearch();
        await searchToggle.FocusAsync();
    }

    private async Task AfterListRenderAsync()
    {
        await SaveListFiltersAsync();
        if (!focusSearch || !searchOpen)
        {
            return;
        }

        focusSearch = false;
        try
        {
            await searchInput.FocusAsync();
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or TaskCanceledException or InvalidOperationException)
        {
            Logger.LogDebug(ex, "Could not focus the boulder search");
        }
    }
}
