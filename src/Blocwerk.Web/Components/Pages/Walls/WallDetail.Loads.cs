// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Helpers;
using Blocwerk.Core.Services;

namespace Blocwerk.Web.Components.Pages.Walls;

/// <summary>
/// The wall page's read model around the load in <c>LoadWall</c>: which carousel page a URL opens, the boulder rows,
/// and the hold-link derived counts, all computed from what one load read.
/// </summary>
public partial class WallDetail
{
    // Carousel pages (0-based): 0 Settings, 1 Members, 2 Overview (centre), 3 Boulders, 4 Details. The overview is
    // the bare URL.
    private static readonly string[] CarouselViews = ["settings", "members", string.Empty, "boulders", "details"];
    private static readonly string CarouselPageViews = string.Join('|', CarouselViews);

    private int InitialCarouselPage
    {
        get
        {
            var page = Array.FindIndex(CarouselViews, v => v.Length > 0 && string.Equals(v, View, StringComparison.OrdinalIgnoreCase));
            return page >= 0 ? page : 2;
        }
    }

    // The prerender pass reads no wall data, but a signed-out visitor still gets the server's redirect to sign in (a
    // 302 on a full load) instead of a page that connects first and then leaves. The same rule LoadWall applies: a
    // share link and a kiosk resting on its own wall view without a user.
    private async Task RedirectSignedOutOnPrerenderAsync()
    {
        if (!string.IsNullOrEmpty(ShareToken))
        {
            return;
        }

        try
        {
            await CurrentUserService.GetCurrentUserAsync();
        }
        catch (UnauthorizedAccessException)
        {
            if (!KioskViewing.AllowsAnonymousViewOf(KioskContext, WallId))
            {
                Navigation.NavigateTo("/account/login", replace: true);
            }
        }
    }

    private async Task LoadBoulderItemsAsync(Wall wall)
    {
        if (!_isAnonymous)
        {
            _items = await FeedbackService.GetBoulderListAsync(WallId);
            return;
        }

        // Anonymous share view: no per-user data, but the rows still name the setters, so
        // pull every setter name for the wall in one query rather than per boulder.
        var setterNames = await WallService.GetBoulderSetterNamesAsync(WallId, ShareToken);
        _items = wall.Boulders
            .Where(b => !b.IsDraft && !b.IsArchived)
            .Select(b => new BoulderListItem(
                b, 0, false, false, false, null, 0, null, setterNames.GetValueOrDefault(b.Id)))
            .ToList();
    }

    /// <summary>
    /// Counts the wall's cross-panel hold links that are actually in force right now, for the card
    /// that tells an editor the linking tool exists. Only worth asking for on a multi-panel wall an
    /// editor is looking at; a failure (or a viewer who cannot enumerate links) simply leaves the
    /// card reporting none.
    /// </summary>
    /// <remarks>
    /// <c>HoldLink</c> rows are wall-level facts between two hold rows and carry no generation, so
    /// the wall-wide row count keeps counting links whose endpoints belong to holds of a superseded
    /// generation. <see cref="Blocwerk.Web.Components.Shared.PanelLinkTool"/> counts only links whose endpoints are holds of the
    /// panels' CURRENT generation, which is why the two disagreed after a wall update. Count what the
    /// tool counts: intersect the link rows with the live panels' live hold ids.
    /// <para>
    /// Both come from what the page already holds: the links read once per load (<see cref="LoadHoldLinksAsync"/>,
    /// shared with the reconciled hold counts) and the wall's holds, which carry every live panel's holds. A live
    /// panel's holds are those at the panel's own generation (as <c>GetPanelHoldsAsync</c> reads them), so no
    /// per-panel query is needed.
    /// </para>
    /// </remarks>
    private void ComputeHoldLinkCount()
    {
        _holdLinkCount = 0;
        if (_livePanels.Count < 2 || !_canEditHolds)
        {
            return;
        }

        var panelGeneration = _livePanels.ToDictionary(p => p.Id, p => p.Generation);
        var liveHoldIds = _holds
            .Where(h => h.WallPanelId is { } panelId && panelGeneration.TryGetValue(panelId, out var generation) && h.Generation == generation)
            .Select(h => h.Id)
            .ToHashSet();
        _holdLinkCount = _holdLinks.Count(l => liveHoldIds.Contains(l.HoldAId) && liveHoldIds.Contains(l.HoldBId));
    }

    // The wall's cross-panel hold links, read once per load for both the link-count card and the reconciled hold
    // counts. A failed read (or a viewer who cannot enumerate links) leaves none: cards fall back to raw counts.
    private IReadOnlyList<HoldLinkPair> _holdLinks = [];

    // Links were made or broken: the card's count and the cards' reconciled hold counts follow.
    private async Task RefreshHoldLinksAsync()
    {
        await LoadHoldLinksAsync();
        ComputeHoldLinkCount();
        ComputeReconciledHoldCounts();
    }

    private async Task<IReadOnlyList<PanelHold>> LoadPanelHolds(Guid panelId) =>
        await WallPanelService.GetPanelHoldsAsync(WallId, panelId, includeStaged: false);

    private async Task LoadHoldLinksAsync()
    {
        try
        {
            _holdLinks = await WallPanelService.GetHoldLinksAsync(WallId);
        }
        catch (Exception)
        {
            _holdLinks = [];
        }
    }

    /// <summary>
    /// Reconciles every boulder's holds over the wall's twin links (read once by the load) to a per-physical-hold
    /// count, so a boulder that saved both twins of a hold is not double-counted on its card. A wall
    /// with no links (single-panel) yields an empty map and the cards fall back to the raw count.
    /// </summary>
    private void ComputeReconciledHoldCounts()
    {
        _reconciledHoldCounts = [];
        _usageCounts = null;
        try
        {
            var links = _holdLinks;
            var linkPairs = links.ToList();

            // The usage heat map counts the same boulders for every viewer (see WallUsageHeat), so a
            // share link shows what a member sees. Kickboard holds count for boulders with kickboard on.
            var kickboardHoldIds = _holds
                .Where(h => h.IsOnKickboard && h.Generation <= (_wall?.CurrentGeneration ?? 0))
                .Select(h => h.Id)
                .ToList();
            _usageCounts = WallUsageHeat.CountByHold(_boulders, linkPairs, kickboardHoldIds);
            if (links.Count == 0)
            {
                return;
            }

            foreach (var boulder in _boulders)
            {
                var reconciled = BoulderHoldReconciler.Reconcile(
                    boulder.BoulderHolds.Select(bh => new ReconcilableHold(bh.HoldId, bh.Type, bh.Usage)),
                    linkPairs);
                _reconciledHoldCounts[boulder.Id] = reconciled.Count;
            }
        }
        catch (Exception)
        {
            // A failed reconcile just leaves cards on their raw count — never blocks the wall page.
            _reconciledHoldCounts = [];
        }
    }
}
