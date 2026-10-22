// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Web.State;
using Microsoft.AspNetCore.Components;

namespace Blocwerk.Web.Components.Pages.Walls;

/// <summary>
/// Coming back to the wall renders it as it was left. Every route change between the wall and a boulder builds a new
/// <see cref="WallDetail"/>, which used to start from "Loading..." and read the whole page again, so back to the list
/// showed a blank page for as long as that took. The left page is remembered per circuit
/// (<see cref="WallPageMemory"/>); the next one restores it before its first render and then reloads as before, so
/// what is on screen is current a moment later.
/// </summary>
public partial class WallDetail
{
    [Inject]
    private WallPageMemory PageMemory { get; set; } = default!;

    // A restored page whose refresh has not landed yet, and an edit tap that arrived meanwhile.
    private bool _refreshing;
    private bool _editAfterRefresh;

    // The wall is gone (deleted from here): nothing of it may be remembered on the way out.
    private bool _pageGone;

    // Set first thing in Dispose: nothing that finishes after it may take a lease or enter edit mode.
    private bool _disposed;

    // Runs the load that follows a restore. Until it lands, entering edit mode is deferred: LoadWall replaces _holds
    // (and the wall) the editor would be dragging. A tap in that window is honoured once the fresh holds are in.
    private async Task RefreshRestoredPageAsync(bool restored, Func<Task> load)
    {
        _refreshing = restored;
        bool wanted;
        try
        {
            await load();
        }
        finally
        {
            // Consumed whatever happened: a failed refresh must not leave a tap to be replayed later.
            _refreshing = false;
            wanted = _editAfterRefresh;
            _editAfterRefresh = false;
        }

        if (wanted && MayEnterEditNow())
        {
            await ToggleEditing();
        }
    }

    // The deferred tap is replayed only while the tap would still be possible: the page is alive (a lease taken on a
    // disposed page is never released, and it holds every deploy) and shows the edit button to this user — the same
    // gate as the button itself (_canEditHolds, not in the 3D view).
    private bool MayEnterEditNow() => !_disposed && _wall is not null && _canEditHolds && !_isEditing && !_show3D;

    private bool DeferEditUntilRefreshed()
    {
        if (!_refreshing)
        {
            return false;
        }

        _editAfterRefresh = true;
        return true;
    }

    private void ForgetPage()
    {
        _pageGone = true;
        PageMemory.Forget(WallId);
    }

    private void RememberPageSafely()
    {
        try
        {
            RememberPage();
        }
        catch (Exception ex)
        {
            // A head start for the next visit, never worth failing a dispose over.
            Logger.LogDebug(ex, "Could not remember the page of wall {WallId}", _loadedWallId);
        }
    }

    // Leaving: remember what is on screen. Never mid-edit (the cloned holds may carry unsaved edits) and never a page
    // that has not loaded.
    private void RememberPage()
    {
        if (!RendererInfo.IsInteractive || _wall is null || _isEditing || _pageGone || _loadedWallId == Guid.Empty)
        {
            return;
        }

        PageMemory.Remember(_loadedWallId, _loadedShareToken, new WallPageSnapshot
        {
            Wall = _wall,
            CanCreateAnonymously = _canCreateAnonymously,
            Holds = _holds,
            BorderPoints = _borderPoints,
            LivePanels = _livePanels,
            Items = _items,
            Boulders = _boulders,
            ReconciledHoldCounts = _reconciledHoldCounts,
            UsageCounts = _usageCounts,
            HoldLinks = _holdLinks,
            HoldLinkCount = _holdLinkCount,
            IsAdmin = _isAdmin,
            IsModerator = _isModerator,
            IsMember = _isMember,
            IsAnonymous = _isAnonymous,
            CurrentUserId = _currentUserId,
            OpenUpdate = _openUpdate,
            ShareLink = _shareLink,
            Members = _members,
            KioskConsented = _kioskConsented,
            ActivityItems = _activityItems,
            ActivityTotal = _activityTotal,
            ActivityPage = _activityPage,
            Filter = new WallListFilter(
                _searchText, _gradeMin, _gradeMax, _fAttempted, _fDone, _fNotDone, _fFavorites, _fCreated, _fBeta, _fNeedsReview, (int)_sort),
        });
    }

    // True when a remembered page was put on screen.
    private bool RestoreFromPageMemory()
    {
        if (PageMemory.Recall<WallPageSnapshot>(WallId, ShareToken) is not { } page)
        {
            return false;
        }

        _wall = page.Wall;
        _canCreateAnonymously = page.CanCreateAnonymously;
        _holds = page.Holds;
        _borderPoints = page.BorderPoints;
        _livePanels = page.LivePanels;
        _items = page.Items;
        _boulders = page.Boulders;
        _reconciledHoldCounts = page.ReconciledHoldCounts;
        _usageCounts = page.UsageCounts;
        _holdLinks = page.HoldLinks;
        _holdLinkCount = page.HoldLinkCount;
        _isAdmin = page.IsAdmin;
        _isModerator = page.IsModerator;
        _isMember = page.IsMember;
        _isAnonymous = page.IsAnonymous;
        _currentUserId = page.CurrentUserId;
        _openUpdate = page.OpenUpdate;
        _shareLink = page.ShareLink;
        _members = page.Members;
        _kioskConsented = page.KioskConsented;
        _activityItems = page.ActivityItems;
        _activityTotal = page.ActivityTotal;
        _activityPage = page.ActivityPage;

        var filter = page.Filter;
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
        _sort = (SortMode)filter.Sort;
        return true;
    }
}
