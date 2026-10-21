// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Feed;

/// <summary>
/// The Postgres horizon: the snapshot's <c>xmin</c>. Every transaction with an xid below it has ended (committed or
/// aborted), and a row that becomes visible later belongs to a transaction with an xid at or above it, so a read capped
/// below <c>xmin</c> never skips a row. <c>xmin</c> never goes backwards, which is why a ceiling taken one statement before
/// the read is still final when the read runs.
/// </summary>
public sealed class PostgresFeedHorizon : IFeedHorizon
{
    public static readonly PostgresFeedHorizon Instance = new();

    // xid8 through text to bigint: no reliance on an Npgsql xid8 mapping. xid8 is epoch-extended and does not wrap.
    private const string XminSql = "SELECT pg_snapshot_xmin(pg_current_snapshot())::text::bigint AS \"Value\"";

    private PostgresFeedHorizon()
    {
    }

    public async Task<FeedPosition> CeilingAsync(BlocwerkDbContext db, CancellationToken cancellationToken)
    {
        var xmin = await db.Database.SqlQueryRaw<long>(XminSql).SingleAsync(cancellationToken);
        return new FeedPosition(xmin - 1, long.MaxValue);
    }

    // EF does not translate a row comparison, so (TxId, Seq) > (a, b) is written out.
    public IQueryable<WallChange> Between(IQueryable<WallChange> source, FeedPosition after, FeedPosition ceiling)
    {
        var afterTx = after.TxId ?? 0;
        var afterSeq = after.Seq;
        var ceilingTx = ceiling.TxId ?? 0;
        var ceilingSeq = ceiling.Seq;
        return source.Where(c =>
            (c.TxId > afterTx || (c.TxId == afterTx && c.Seq > afterSeq))
            && (c.TxId < ceilingTx || (c.TxId == ceilingTx && c.Seq <= ceilingSeq)));
    }

    public IQueryable<WallChange> InOrder(IQueryable<WallChange> source) => source.OrderBy(c => c.TxId).ThenBy(c => c.Seq);
}
