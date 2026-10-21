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

    // Share-link views read through the uncached share path and are not live, as with Cache.Changed. Held, besides the
    // inline forms, until the page's own load has a boulder: it releases them after (ReleaseStats).
    private void OpenStatsFeed()
    {
        _statsFeed?.Dispose();
        _statsFeed = string.IsNullOrEmpty(ShareToken)
            ? LeafFeed.Open(
                Cache,
                new LeafScope(WallId, BoulderId, StatsKinds),
                InvokeAsync,
                ApplyStatsAsync,
                () => _editMeta || _showGradeForm || _boulder is null,
                Logger)
            : null;
    }

    // Called on the dispatcher when the last inline form closed, or the page's load finished: applies what was held.
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

    // On the dispatcher. Through the feed, so the counts land only for the boulder they were read for (a same-template
    // navigation disposes this feed and drops them), and only the latest refresh lands.
    private async Task ApplyStatsAsync(LeafBatch batch)
    {
        if (_statsFeed is not { } feed)
        {
            return;
        }

        var kinds = batch.Reload ? StatsKinds : batch.Events.Select(e => e.Kind).ToHashSet();
        var boulderId = BoulderId;
        var anonymous = _isAnonymous;
        try
        {
            await feed.LoadAsync(() => ReadStatsAsync(boulderId, anonymous, kinds), stats =>
            {
                if (BoulderId == boulderId && _boulder?.Id == boulderId)
                {
                    ApplyStats(stats);
                }
            });
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "A live stats refresh of boulder {BoulderId} failed; the counts stay as they are", boulderId);
        }

        StateHasChanged();
    }

    private async Task<BoulderStatsRead> ReadStatsAsync(Guid boulderId, bool anonymous, IReadOnlySet<string> kinds)
    {
        var read = new BoulderStatsRead();
        if (kinds.Contains(FeedKinds.BoulderStats))
        {
            read.ProposalRead = true;
            read.Proposal = await BoulderService.GetActiveProposalAsync(boulderId);
        }

        if (anonymous)
        {
            return read;
        }

        if (kinds.Contains(FeedKinds.TickMine))
        {
            read.Attempts = await AttemptService.GetAttemptsForBoulderAsync(boulderId);
            read.Summary = await AttemptService.GetBoulderSummaryForUserAsync(boulderId);
        }

        if (kinds.Contains(FeedKinds.BoulderStats) || kinds.Contains(FeedKinds.RatingMine))
        {
            read.Rating = await FeedbackService.GetRatingAsync(boulderId);
        }

        if (kinds.Contains(FeedKinds.FavoriteChanged))
        {
            read.Favorite = await FeedbackService.IsFavoritedAsync(boulderId);
        }

        return read;
    }

    private void ApplyStats(BoulderStatsRead read)
    {
        if (read.ProposalRead)
        {
            _activeProposal = read.Proposal;
        }

        _attempts = read.Attempts ?? _attempts;
        _summary = read.Summary ?? _summary;
        _rating = read.Rating ?? _rating;
        _isFavorite = read.Favorite ?? _isFavorite;
    }
}
