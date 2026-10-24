// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;

namespace Blocwerk.Client.Sync;

/// <summary>
/// The closed stream's catch-up and the reloads (plan §1, sync loop step 3): <c>/changes</c> from the cursor, page by
/// page while <c>more</c>, then the stream again. A 429 waits <c>Retry-After</c> plus 0–20 %; a refusal goes to the
/// identity matrix; no network backs off. A backlog longer than the stream's own replay window (4 pages) is not paged
/// through: the stream opens without a cursor and the server resyncs. A reload that did not reach the server keeps the
/// cursor, so nothing between the documents and the server's mark is skipped.
/// </summary>
internal sealed partial class WallSync
{
    // The stream replays at most 4 pages of 500 itself (FeedStreamSession.MaxCatchUpPages); past that, a resync is cheaper.
    private const int MaxCatchUpPages = 4;

    private async Task CatchUpAsync(int mine)
    {
        var pages = 0;
        while (mine == run)
        {
            var page = await transport.ChangesAsync(WallId, cursor, Token);
            if (mine != run)
            {
                return;
            }

            if (page.Status == 429)
            {
                await wait.WaitAsync(SyncDelays.RetryAfter(page.RetryAfter, clock.NextJitter()), isNetwork: false);
                continue;
            }

            if (SyncLabels.IsRefusal(page.Status))
            {
                Stop();
                await refresh.RefusedAsync(page.Status, page.Reason);
                return;
            }

            var changes = page.Status == 200 ? SyncJson.Read(page.Body, SyncJsonContext.Default.FeedChanges) : null;
            if (changes is null)
            {
                await backoff.FailedAsync();
                continue;
            }

            backoff.Reached();
            if (changes.More && !changes.Resync && ++pages >= MaxCatchUpPages)
            {
                cursor = null;
                break;
            }

            if (await PageAsync(mine, changes))
            {
                break;
            }
        }

        await ReopenAsync(mine);
    }

    // A stream that was refused (a cap's hidden 429) or blocked while /changes answered waits 30 s or more before the
    // next try, never a loop at network speed.
    private async Task ReopenAsync(int mine)
    {
        if (QuickFailures > 0 && mine == run)
        {
            await wait.WaitAsync(SyncDelays.StreamRefused(QuickFailures, clock.NextJitter()), isNetwork: false);
        }

        if (mine == run)
        {
            Open();
        }
    }

    // One catch-up page; true when it was the last one.
    private async Task<bool> PageAsync(int mine, FeedChanges changes)
    {
        if (changes.Resync)
        {
            if (await ResyncAsync(mine, changes.Next))
            {
                return true;
            }

            await backoff.FailedAsync();
            return false;
        }

        foreach (var envelope in changes.Events)
        {
            if (mine != run)
            {
                return true;
            }

            await ApplyAsync(mine, envelope);
        }

        if (mine == run && SyncCursor.IsAfter(changes.Next, cursor))
        {
            cursor = changes.Next;
            Settle();
        }

        return !changes.More;
    }

    // The stream's resync event: reload and continue; a reload that did not reach the server probes again, backing off.
    private async Task StreamResyncAsync(int mine, string next)
    {
        if (await ResyncAsync(mine, next) || mine != run)
        {
            return;
        }

        CloseStream();
        await backoff.FailedAsync();
        await CatchUpAsync(mine);
    }

    // Reload, then continue after the server's mark, but only when the reload reached the server.
    private async Task<bool> ResyncAsync(int mine, string next)
    {
        if (!await ReloadAsync(mine))
        {
            return false;
        }

        if (mine == run)
        {
            cursor = next;
            Settle();
        }

        return true;
    }

    // True when the reload's documents reached the server; what it brought is then no longer owed (marks that did not
    // arrive stay owed until a later refetch lands).
    private async Task<bool> ReloadAsync(int mine)
    {
        var (index, owedMarks) = (indexWanted, mineWanted);
        var (keep, reached, marks, _) = await refresh.RunAsync();
        if (!keep)
        {
            StopIf(mine);
            return false;
        }

        if (reached)
        {
            indexDone = Math.Max(indexDone, index);
        }

        if (marks)
        {
            mineDone = Math.Max(mineDone, owedMarks);
        }
        else
        {
            mineWanted++;
        }

        return reached;
    }
}
