// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Feed;

/// <summary>What a mapper sees of the save it maps: the context, the actor, the time, and the rows collected so far.</summary>
public sealed class FeedMapContext
{
    private readonly List<WallChange> rows = [];
    private readonly Dictionary<Guid, FeedBoulderTouch> touched = [];

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

    /// <summary>Gets the boulders whose parts changed (<see cref="TouchBoulder"/>), one entry per boulder.</summary>
    public IReadOnlyCollection<FeedBoulderTouch> TouchedBoulders => touched.Values;

    /// <summary>Adds a row, stamping the save's actor and time where the mapper left them unset.</summary>
    public void Add(WallChange row)
    {
        row.ActorUserId ??= ActorUserId;
        row.CreatedAt = Now;
        rows.Add(row);
    }

    /// <summary>
    /// Adds a row that names no actor, whoever saved: a coarse event any viewer sees (<c>boulder.stats</c>) must not tell
    /// them who just rated or ticked.
    /// </summary>
    public void AddWithoutActor(WallChange row)
    {
        row.ActorUserId = null;
        row.CreatedAt = Now;
        rows.Add(row);
    }

    /// <summary>
    /// Records that a part of a boulder changed (its holds, setters or move hints). After the save is mapped each touched
    /// boulder gets one <c>boulder.changed</c> (<see cref="FeedBoulderResolver"/>), unless the boulder wrote its own row.
    /// Pass the boulder when it is tracked (the part's navigation), and its wall when the part knows it. A boulder that is
    /// neither tracked nor inside a scope is looked up once per save, never per part.
    /// </summary>
    public void TouchBoulder(Guid boulderId, Boulder? boulder, Guid? wallId) => Touch(boulderId, boulder, wallId, mappingFailed: false);

    /// <summary>Records that a mapper threw on a part of the boulder: the resolver resyncs the boulder's wall.</summary>
    internal void FailBoulder(Guid boulderId, Boulder? boulder) => Touch(boulderId, boulder, null, mappingFailed: true);

    /// <summary>Drops the rows added after <paramref name="count"/>: a mapper that threw half-way leaves nothing behind.</summary>
    internal void TruncateTo(int count) => rows.RemoveRange(count, rows.Count - count);

    private void Touch(Guid boulderId, Boulder? boulder, Guid? wallId, bool mappingFailed)
    {
        var known = touched.GetValueOrDefault(boulderId);
        var tracked = known?.Boulder ?? boulder;
        var wall = known?.WallId ?? wallId ?? tracked?.WallId;
        touched[boulderId] = new FeedBoulderTouch(boulderId, tracked, wall, mappingFailed || known?.MappingFailed == true);
    }
}
