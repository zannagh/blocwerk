// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Blocwerk.Core.Services;

/// <summary>
/// Seq numbering for the batches several writers resume at once (the wall-update batch: staging, every staged-hold
/// touch-up, the promote; appended audit actions). Seq is unique per batch, and each writer used to seed its own counter
/// with "max + 1" when it opened its scope, so two scopes open at the same time handed out the same numbers and the
/// second save failed on the unique index. Their numbers now come from one counter per batch, shared by every scope of
/// it in this process and reserved at save time.
/// </summary>
/// <remarks>
/// Single-instance assumption, as for the rest of the app's in-process coordination: two app instances writing to one
/// open batch could still collide. A failed or rolled-back save leaves a gap, which is harmless: Seq only orders.
/// </remarks>
public sealed partial class ChangeJournal
{
    private readonly ConcurrentDictionary<Guid, StrongBox<int>> sharedSeq = new();

    /// <summary>
    /// Reserves <paramref name="count"/> consecutive Seq numbers for one save into <paramref name="scope"/>'s batch and
    /// returns the first. A batch whose row already existed when the scope opened is resumable by others, so it draws
    /// from the shared counter, seeded no lower than the scope's own start (the database's max + 1 when it opened).
    /// </summary>
    internal int ReserveSeq(ChangeJournalBatchScope scope, int count)
    {
        if (!scope.Resumable)
        {
            var own = scope.NextSeq;
            scope.NextSeq += count;
            return own;
        }

        var counter = sharedSeq.GetOrAdd(scope.BatchId, _ => new StrongBox<int>(0));
        lock (counter)
        {
            var start = Math.Max(counter.Value, scope.NextSeq);
            counter.Value = start + count;
            scope.NextSeq = counter.Value;
            return start;
        }
    }

    /// <summary>Forgets a closed batch's counter (it takes no more writes).</summary>
    private void ForgetSeq(Guid batchId) => sharedSeq.TryRemove(batchId, out _);
}
