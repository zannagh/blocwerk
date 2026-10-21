// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Feed;

/// <summary>
/// The SQLite (test) horizon: no transaction ids, and the harness serialises writes, so rows are ordered by <c>Seq</c>
/// alone and the ceiling is the newest committed <c>Seq</c>. The null <c>TxId</c> of a position is ignored throughout.
/// </summary>
public sealed class SqliteFeedHorizon : IFeedHorizon
{
    public static readonly SqliteFeedHorizon Instance = new();

    private SqliteFeedHorizon()
    {
    }

    public async Task<FeedPosition> CeilingAsync(BlocwerkDbContext db, CancellationToken cancellationToken)
    {
        var newest = await db.WallChanges.MaxAsync(c => (long?)c.Seq, cancellationToken);
        return new FeedPosition(null, newest ?? 0);
    }

    public IQueryable<WallChange> Between(IQueryable<WallChange> source, FeedPosition after, FeedPosition ceiling)
    {
        var afterSeq = after.Seq;
        var ceilingSeq = ceiling.Seq;
        return source.Where(c => c.Seq > afterSeq && c.Seq <= ceilingSeq);
    }

    public IQueryable<WallChange> InOrder(IQueryable<WallChange> source) => source.OrderBy(c => c.Seq);
}
