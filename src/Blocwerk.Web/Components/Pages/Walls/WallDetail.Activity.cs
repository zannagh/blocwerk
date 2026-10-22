// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Feed;
using Blocwerk.Web.State.Live;

namespace Blocwerk.Web.Components.Pages.Walls;

/// <summary>
/// The wall's activity card, for members only: a share link and a kiosk tablet with nobody picked get "Log in to see"
/// (the tablet reads activity per boulder instead). Bylines go through the feed's author rule (<see cref="LeafAuthors"/>),
/// so an erased or system author reads as Ghost.
/// </summary>
public partial class WallDetail
{
    private async Task LoadActivity(int page)
    {
        _activityPage = page;
        var (items, total) = await ActivityLogService.GetWallActivityAsync(WallId, page);
        _activityItems = items;
        _activityTotal = total;
    }

    private string ActivityAuthor(ActivityLogEntry item) =>
        LeafAuthors.Describe(item.User, FeedViewer.Member(_currentUserId)).Name;
}
