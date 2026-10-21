// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Collections.Immutable;

namespace Blocwerk.Core.Feed;

/// <summary>
/// The hub's state of one subscribed wall: its subscribers, its cursor, and when it is read next. The cursor is read and
/// moved only under <see cref="Gate"/>, which a subscribe takes too, so a new subscriber's start and the rows it is
/// handed never leave a gap. Wake and schedule fields belong to the hub's loop thread.
/// </summary>
internal sealed class FeedHubWall
{
    private readonly Lock sync = new();
    private ImmutableArray<FeedHubSubscriber> subscribers = [];
    private bool removed;

    public FeedHubWall(Guid wallId)
    {
        WallId = wallId;
    }

    public Guid WallId { get; }

    /// <summary>Gets the lock around every read and every subscribe of this wall.</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>Gets or sets the hub's place in the wall's rows; null until the first subscriber took it.</summary>
    public FeedCursor? Cursor { get; set; }

    /// <summary>Gets when the loop reads the wall next.</summary>
    public DateTimeOffset DueAt { get; private set; } = DateTimeOffset.MinValue;

    /// <summary>Gets the newest woken transaction not yet seen below the horizon, if a wake is outstanding.</summary>
    public long? WokenTx { get; private set; }

    public bool IsWoken { get; private set; }

    public int Repolls { get; private set; }

    public ImmutableArray<FeedHubSubscriber> Subscribers => subscribers;

    public bool IsRemoved
    {
        get
        {
            lock (sync)
            {
                return removed;
            }
        }
    }

    /// <summary>Adds a subscriber; false when the wall was removed meanwhile (the caller starts over with a fresh one).</summary>
    public bool TryAdd(FeedHubSubscriber subscriber)
    {
        lock (sync)
        {
            if (removed)
            {
                return false;
            }

            subscribers = subscribers.Add(subscriber);
            return true;
        }
    }

    /// <summary>Removes a subscriber (null: none); true when the wall has none left and is now removed.</summary>
    public bool RemoveAndRetireIfEmpty(FeedHubSubscriber? subscriber)
    {
        lock (sync)
        {
            if (subscriber is not null)
            {
                subscribers = subscribers.Remove(subscriber);
            }

            removed = subscribers.IsEmpty;
            return removed;
        }
    }

    /// <summary>A wake for this wall: read it now.</summary>
    public void Wake(long? txId, DateTimeOffset now)
    {
        IsWoken = true;
        WokenTx = txId is { } tx && (WokenTx is not { } known || tx > known) ? tx : WokenTx;
        Repolls = 0;
        DueAt = now;
    }

    /// <summary>
    /// After a read that ended at <paramref name="cursor"/>: a woken transaction still above it is not yet below the horizon,
    /// so the wall is re-polled with backoff; otherwise the wake is done and the safety poll is next.
    /// </summary>
    public void AfterRead(FeedCursor cursor, DateTimeOffset now)
    {
        Cursor = cursor;
        var held = IsWoken && WokenTx is { } woken && cursor.Position.TxId is { } seen && seen < woken;
        if (held)
        {
            Repolls++;
            DueAt = now + FeedRepoll.Delay(Repolls);
            return;
        }

        IsWoken = false;
        WokenTx = null;
        Repolls = 0;
        DueAt = now + FeedRepoll.SafetyPoll;
    }

    /// <summary>A read that failed (the database is away): try again with backoff.</summary>
    public void AfterFailure(DateTimeOffset now)
    {
        Repolls++;
        DueAt = now + FeedRepoll.Delay(Repolls);
    }
}
