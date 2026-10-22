// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Feed;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Assets.DataDocs;

/// <summary>
/// Whether a stored document still reflects its wall: the epoch is the one it was built in, its cursor is not below the
/// retention floor (a client would resync on it forever), and the wall has no row of the document's input kinds after
/// its build mark (one query on the <c>(WallId, TxId, Seq)</c> index), nor a <c>user.profile</c> row of a person it names
/// by byline, on any wall (one query on the <c>(EntityId, Kind)</c> index): the wall-wide rename row reaches only the walls
/// a user belongs to, and a setter who left the wall is still credited on it.
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
        var horizon = FeedHorizons.For(db);
        var rows = db.WallChanges.AsNoTracking().Where(c => c.WallId == wallId && kinds.Contains(c.Kind));
        if (await horizon.Between(rows, document.BuildMark.Position, End).AnyAsync(ct))
        {
            return false;
        }

        if (document.People.Count == 0)
        {
            return true;
        }

        var people = document.People.ToArray();
        var renames = db.WallChanges.AsNoTracking()
            .Where(c => c.EntityId != null && people.Contains(c.EntityId.Value) && c.Kind == FeedKinds.UserProfile);
        return !await horizon.Between(renames, document.BuildMark.Position, End).AnyAsync(ct);
    }

    // As the feed reads it: a cursor exactly at the floor lost nothing, one below it may have.
    private static bool IsBelowFloor(FeedPosition position, FeedEpoch epoch) =>
        epoch.FloorSeq > 0 && position < new FeedPosition(epoch.FloorTxId, epoch.FloorSeq);
}
