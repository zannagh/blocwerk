// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Feed;

/// <summary>What a mapper sees of the save it maps: the context, the actor, the time, and the rows collected so far.</summary>
public sealed class FeedMapContext
{
    private readonly List<WallChange> rows = [];

    public FeedMapContext(DbContext context, Guid? actorUserId, DateTimeOffset now)
    {
        Context = context;
        ActorUserId = actorUserId;
        Now = now;
    }

    /// <summary>Gets the saving context (to look at other tracked entries, never to query).</summary>
    public DbContext Context { get; }

    /// <summary>Gets the signed-in user behind the save, or null for background work.</summary>
    public Guid? ActorUserId { get; }

    public DateTimeOffset Now { get; }

    /// <summary>Gets the rows collected so far, in mapping order.</summary>
    public IReadOnlyList<WallChange> Rows => rows;

    /// <summary>Adds a row, stamping the save's actor and time where the mapper left them unset.</summary>
    public void Add(WallChange row)
    {
        row.ActorUserId ??= ActorUserId;
        row.CreatedAt = Now;
        rows.Add(row);
    }

    /// <summary>Drops the rows added after <paramref name="count"/>: a mapper that threw half-way leaves nothing behind.</summary>
    internal void TruncateTo(int count) => rows.RemoveRange(count, rows.Count - count);
}
