// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Feed;
using Blocwerk.Web.State.Live;

namespace Blocwerk.Web.Components.Pages.Walls;

/// <summary>
/// The wall's activity card. Members read the whole log; the wall's own kiosk tablet with nobody picked reads it too,
/// read-only and narrowed by the service (<c>KioskActivityVisibility</c>); a share link does not get it. Bylines go
/// through the feed's author rule (<see cref="LeafAuthors"/>), so an erased or system author reads as Ghost.
/// </summary>
public partial class WallDetail
{
    /// <summary>True for the resting kiosk on its own wall, the one anonymous viewer the activity card is for.</summary>
    private bool ReadsKioskActivity =>
        string.IsNullOrEmpty(ShareToken) && KioskViewing.AllowsAnonymousViewOf(KioskContext, WallId);

    private async Task LoadActivity(int page)
    {
        _activityPage = page;
        var (items, total) = await ActivityLogService.GetWallActivityAsync(WallId, page);
        _activityItems = items;
        _activityTotal = total;
    }

    private string ActivityAuthor(ActivityLogEntry item) =>
        LeafAuthors.Describe(item.User, _isAnonymous ? FeedViewer.Kiosk() : FeedViewer.Member(_currentUserId)).Name;
}
