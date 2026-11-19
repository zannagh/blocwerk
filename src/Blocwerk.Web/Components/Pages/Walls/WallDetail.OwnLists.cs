// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Helpers;

namespace Blocwerk.Web.Components.Pages.Walls;

/// <summary>
/// The viewer's own lists on the browser's landing (My projects, Favorites) and their <c>?mine=</c> level. Only a
/// signed-in viewer on the wall itself has them: a share link shows the wall as its link holder sees it, and a kiosk is a
/// shared tablet, so neither gets the cards or the level, even with someone signed in.
/// </summary>
public partial class WallDetail
{
    private IReadOnlyDictionary<BoulderMine, int>? mineCounts;
    private (object? Items, bool Own) mineCountsKey;

    private bool HasOwnLists => !_isAnonymous && string.IsNullOrEmpty(ShareToken) && !KioskContext.IsKiosk;

    // Counted as families like the other cards, once per new list (_items is always reassigned, never mutated).
    private IReadOnlyDictionary<BoulderMine, int>? MineCounts
    {
        get
        {
            var key = ((object?)_items, HasOwnLists);
            if (mineCountsKey != key)
            {
                mineCountsKey = key;
                mineCounts = HasOwnLists ? CountOwnLists() : null;
            }

            return mineCounts;
        }
    }

    private Dictionary<BoulderMine, int> CountOwnLists()
    {
        int projects = 0, favorites = 0;
        foreach (var item in VisibleItems())
        {
            projects += AnyMember(item, m => m.IsProject) ? 1 : 0;
            favorites += AnyMember(item, m => m.IsFavorite) ? 1 : 0;
        }

        return new() { [BoulderMine.Projects] = projects, [BoulderMine.Favorites] = favorites };
    }
}
