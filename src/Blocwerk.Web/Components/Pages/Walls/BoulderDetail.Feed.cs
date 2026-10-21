// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Web.State.Live;

namespace Blocwerk.Web.Components.Pages.Walls;

/// <summary>
/// The boulder page's live counts (design §5.5). <c>boulder.stats</c> is coarse (ticks, ratings or grade proposals of the
/// boulder changed): it re-queries the rating summary and the open grade proposal, computed at read time. The viewer's
/// own rows (<c>tick.mine</c>, <c>rating.mine</c>, <c>favorite.changed</c>, from their other devices; never on a kiosk)
/// re-query their history, summary, rating and favourite. Held while the name/grade or the proposal form is open, so a
/// refresh never replaces what the user is choosing; applied when the form closes.
/// </summary>
public partial class BoulderDetail
{
    private static readonly IReadOnlySet<string> StatsKinds = new HashSet<string>
    {
        FeedKinds.BoulderStats, FeedKinds.TickMine, FeedKinds.RatingMine, FeedKinds.FavoriteChanged,
    };

    private LeafFeed? _statsFeed;

    // Share-link views read through the uncached share path and are not live, as with Cache.Changed.
    private void OpenStatsFeed()
    {
        _statsFeed?.Dispose();
        _statsFeed = string.IsNullOrEmpty(ShareToken)
            ? LeafFeed.Open(Cache, new LeafScope(WallId, BoulderId, StatsKinds), InvokeAsync, ApplyStatsAsync, () => _editMeta || _showGradeForm)
            : null;
    }

    // Called on the dispatcher when the last inline form closed: applies what arrived while it was open.
    private void ReleaseStats() => _ = ReleaseStatsAsync();

    private async Task ReleaseStatsAsync()
    {
        try
        {
            await InvokeAsync(() => _statsFeed?.ReleaseAsync() ?? Task.CompletedTask);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Applying the held stats of boulder {BoulderId} failed", BoulderId);
        }
    }

    // On the dispatcher.
    private async Task ApplyStatsAsync(LeafBatch batch)
    {
        if (_boulder is null)
        {
            return; // the page's own load is still running and reads the newest counts
        }

        var kinds = batch.Reload ? StatsKinds : batch.Events.Select(e => e.Kind).ToHashSet();
        try
        {
            await RefreshStatsAsync(kinds);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "A live stats refresh of boulder {BoulderId} failed; the counts stay as they are", BoulderId);
        }

        StateHasChanged();
    }

    private async Task RefreshStatsAsync(IReadOnlySet<string> kinds)
    {
        if (kinds.Contains(FeedKinds.BoulderStats))
        {
            _activeProposal = await BoulderService.GetActiveProposalAsync(BoulderId);
        }

        if (_isAnonymous)
        {
            return;
        }

        if (kinds.Contains(FeedKinds.TickMine))
        {
            _attempts = await AttemptService.GetAttemptsForBoulderAsync(BoulderId);
            _summary = await AttemptService.GetBoulderSummaryForUserAsync(BoulderId);
        }

        if (kinds.Contains(FeedKinds.BoulderStats) || kinds.Contains(FeedKinds.RatingMine))
        {
            _rating = await FeedbackService.GetRatingAsync(BoulderId);
        }

        if (kinds.Contains(FeedKinds.FavoriteChanged))
        {
            _isFavorite = await FeedbackService.IsFavoritedAsync(BoulderId);
        }
    }
}
