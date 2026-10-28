// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Client.Store;
using Blocwerk.Contracts.Feed;

namespace Blocwerk.Client.Sync;

/// <summary>
/// Applying events (plan §1, sync loop step 2): each once, unless the document it changes was built after it, then the
/// follow-ups, debounced per wall; and the resume cursor, which moves only once nothing applied is still owed.
/// </summary>
internal sealed partial class WallSync
{
    private static string? DocumentOf(string kind) => kind switch
    {
        FeedKinds.BoulderChanged or FeedKinds.BoulderDeleted => "boulders",
        FeedKinds.CommentAdded or FeedKinds.CommentDeleted => "comments",
        _ => null,
    };

    // Each event once: the replay after a reconnect and a hub that already tracked the wall can both deliver a row.
    private async Task ApplyAsync(int mine, FeedEnvelope envelope)
    {
        if (cursor is not null && !SyncCursor.IsAfter(envelope.Cursor, cursor))
        {
            return;
        }

        var followUp = SyncPayload.IsUnknown(envelope) ? FeedFollowUp.Resync
            : InItsDocument(envelope) ? FeedFollowUp.None
            : store.Apply(Partition, envelope);
        cursor = envelope.Cursor;
        if (followUp.HasFlag(FeedFollowUp.Resync))
        {
            if (!await ReloadAsync(mine) && mine == run)
            {
                // The reload did not reach the server: the documents stay owed until the index answers.
                indexWanted++;
                indexDebounce.Schedule(() => queue.Enqueue(() => IndexAsync(mine)));
            }

            Settle();
            return;
        }

        if (followUp.HasFlag(FeedFollowUp.RefetchIndex))
        {
            indexWanted++;
            indexDebounce.Schedule(() => queue.Enqueue(() => IndexAsync(mine)));
        }

        if (followUp.HasFlag(FeedFollowUp.RefetchMine))
        {
            mineWanted++;
            mineDebounce.Schedule(() => queue.Enqueue(() => MineAsync(mine)));
        }

        Settle();
    }

    // A replayed row older than the document it changes is already in it: applying it again would, say, hide a
    // boulder that was unpublished and republished before the document was built.
    private bool InItsDocument(FeedEnvelope envelope) =>
        DocumentOf(envelope.Kind) is { } kind
        && refresh.DocCursors.TryGetValue(kind, out var built)
        && !SyncCursor.IsAfter(envelope.Cursor, built);

    // Everything applied is on the device once no index or marks refetch is owed: the resume cursor moves.
    private void Settle()
    {
        if (indexWanted == indexDone && mineWanted == mineDone)
        {
            keeper.Advance(cursor);
        }
    }

    private async Task IndexAsync(int mine)
    {
        if (mine != run)
        {
            return;
        }

        var owed = indexWanted;
        var (keep, reached) = await refresh.IndexAsync();
        if (!keep)
        {
            StopIf(mine);
            return;
        }

        if (reached)
        {
            indexDone = Math.Max(indexDone, owed);
            Settle();
        }
    }

    private async Task MineAsync(int mine)
    {
        if (mine != run)
        {
            return;
        }

        var owed = mineWanted;
        if (await refresh.MineAsync())
        {
            mineDone = Math.Max(mineDone, owed);
            Settle();
        }
    }
}
