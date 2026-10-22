// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Feed;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Assets.DataDocs;

/// <summary>
/// Whether a stored document still reflects its wall: the epoch is the one it was built in, its cursor is not below the
/// retention floor (a client would resync on it forever), and the wall has no row of the document's input kinds after
/// its build mark. The last is one query on the <c>(WallId, TxId, Seq)</c> index.
/// </summary>
/// <remarks>
/// The rows are read to the end of the feed, not up to the horizon: a committed row above the horizon already says the
/// document is stale, and a row still in flight lands after the build mark (the mark was read below the horizon), so it is
/// seen once it commits. Every audience's rows count, so an outsider's document is rebuilt on a setters-only row too: the
/// rebuild moves its build mark past that row, and a row that changes nothing it shows yields the same sha.
/// </remarks>
internal static class DataDocumentFreshness
{
    private static readonly FeedPosition End = new(long.MaxValue, long.MaxValue);

    public static async Task<bool> IsFreshAsync(BlocwerkDbContext db, StoredDataDocument document, CancellationToken ct)
    {
        var epoch = await FeedEpochStore.ReadAsync(db, ct);
        if (epoch is null || epoch.Id != document.BuildMark.Epoch || IsBelowFloor(document.Cursor.Position, epoch))
        {
            return false;
        }

        var wallId = document.WallId;
        var kinds = DataDocumentInputs.Of(document.Kind).ToArray();
        var rows = db.WallChanges.AsNoTracking().Where(c => c.WallId == wallId && kinds.Contains(c.Kind));
        return !await FeedHorizons.For(db).Between(rows, document.BuildMark.Position, End).AnyAsync(ct);
    }

    // As the feed reads it: a cursor exactly at the floor lost nothing, one below it may have.
    private static bool IsBelowFloor(FeedPosition position, FeedEpoch epoch) =>
        epoch.FloorSeq > 0 && position < new FeedPosition(epoch.FloorTxId, epoch.FloorSeq);
}
