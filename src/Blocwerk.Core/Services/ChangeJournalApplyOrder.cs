// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Services;

/// <summary>
/// The forward apply order of a package's entries: the order the source database actually saw the writes.
/// </summary>
/// <remarks>
/// Each batch is a stream already ordered by its own <c>Seq</c>, and that order is never broken. The streams are
/// merged by the time each entry was written, so a batch that stayed open across an update (the wall-update batch is
/// created at staging and written again at promote) interleaves correctly with the batches written in between (an
/// "adhoc" touch-up recorded before this fix, a look-over answered later). Ties, which happen for the entries of one
/// SaveChanges, fall back to the batch's creation order. Concatenating whole batches instead put every touch-up after
/// the promote that consumed it (review finding C7).
/// </remarks>
internal static class ChangeJournalApplyOrder
{
    /// <summary>Merges the batches' Seq-ordered entry streams by write time; <paramref name="batchOrder"/> breaks ties.</summary>
    public static List<ChangeJournalEntry> Merge(
        IEnumerable<ChangeJournalEntry> entries, IReadOnlyDictionary<Guid, int> batchOrder)
    {
        var streams = entries
            .GroupBy(e => e.BatchId)
            .Select(g => new Queue<ChangeJournalEntry>(g.OrderBy(e => e.Seq)))
            .ToList();

        var merged = new List<ChangeJournalEntry>(streams.Sum(s => s.Count));
        while (streams.Count > 0)
        {
            var next = streams
                .OrderBy(s => s.Peek().CreatedAt)
                .ThenBy(s => batchOrder.TryGetValue(s.Peek().BatchId, out var i) ? i : int.MaxValue)
                .First();
            merged.Add(next.Dequeue());
            if (next.Count == 0)
            {
                streams.Remove(next);
            }
        }

        return merged;
    }
}
