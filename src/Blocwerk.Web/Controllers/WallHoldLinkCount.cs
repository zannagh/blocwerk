// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Services;

namespace Blocwerk.Web.Controllers;

/// <summary>
/// Counts the web wall page's cross-panel link reminder: the live panels, and the link rows whose two holds are both live
/// holds of a live panel (what <c>WallDetail.ComputeHoldLinkCount</c> and the link tool count). Links whose holds belong to
/// a superseded generation are left out.
/// </summary>
public static class WallHoldLinkCount
{
    /// <summary>The reminder's numbers for <paramref name="wallId"/>, read through the page's own panel service.</summary>
    public static async Task<WallHoldLinksResponse> CountAsync(IWallPanelService panels, Guid wallId, bool linksFinalized)
    {
        var live = (await panels.GetPanelsAsync(wallId)).Where(p => p.IsLive).ToList();
        if (live.Count < 2)
        {
            return new WallHoldLinksResponse(live.Count, 0, linksFinalized);
        }

        var liveHoldIds = new HashSet<Guid>();
        foreach (var panel in live)
        {
            foreach (var hold in await panels.GetPanelHoldsAsync(wallId, panel.Id, includeStaged: false))
            {
                liveHoldIds.Add(hold.Id);
            }
        }

        var links = await panels.GetHoldLinksAsync(wallId);
        var count = links.Count(l => liveHoldIds.Contains(l.HoldAId) && liveHoldIds.Contains(l.HoldBId));
        return new WallHoldLinksResponse(live.Count, count, linksFinalized);
    }
}
