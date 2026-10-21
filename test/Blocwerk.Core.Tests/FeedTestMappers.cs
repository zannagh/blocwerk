// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Contracts.Feed.Payloads;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Blocwerk.Core.Tests;

/// <summary>A stand-in hold mapper (the real one is PR 1.2b): one <c>hold.changed</c>/<c>hold.deleted</c> per hold.</summary>
internal sealed class ProbeHoldMapper : IFeedMapper
{
    public IReadOnlyCollection<Type> EntityTypes { get; } = [typeof(Hold)];

    public void Map(EntityEntry entry, FeedMapContext context)
    {
        var hold = (Hold)entry.Entity;
        context.Add(new WallChange
        {
            WallId = hold.WallId,
            Kind = entry.State == EntityState.Deleted ? FeedKinds.HoldDeleted : FeedKinds.HoldChanged,
            EntityId = hold.Id,
            DataJson = FeedJson.Serialize(new HoldPayload(hold.Generation, hold.WallPanelId)),
        });
    }
}

/// <summary>A user-profile stand-in: a wall-less <see cref="FeedAudience.User"/> row.</summary>
internal sealed class ProbeUserMapper : IFeedMapper
{
    public IReadOnlyCollection<Type> EntityTypes { get; } = [typeof(User)];

    public void Map(EntityEntry entry, FeedMapContext context)
    {
        var user = (User)entry.Entity;
        context.Add(new WallChange
        {
            Kind = FeedKinds.UserProfile, EntityId = user.Id, Audience = FeedAudience.User, AudienceUserId = user.Id,
        });
    }
}

/// <summary>Adds a row and then throws, like a mapper with a bug half-way through.</summary>
internal sealed class ThrowingMapper : IFeedMapper
{
    public ThrowingMapper(params Type[] types)
    {
        EntityTypes = types;
    }

    public IReadOnlyCollection<Type> EntityTypes { get; }

    public void Map(EntityEntry entry, FeedMapContext context)
    {
        context.Add(new WallChange { WallId = Guid.NewGuid(), Kind = "half.written" });
        throw new InvalidOperationException("mapper bug");
    }
}

/// <summary>Records the wakes instead of delivering them.</summary>
internal sealed class RecordingFeedSignal : IFeedSignal
{
    private readonly InProcessFeedSignal inner = new();

    public List<FeedWake> Woken { get; } = [];

    public System.Threading.Channels.ChannelReader<FeedWake> Wakes => inner.Wakes;

    public void Wake(FeedWake wake)
    {
        lock (Woken)
        {
            Woken.Add(wake);
        }

        inner.Wake(wake);
    }
}

/// <summary>Counts how often it is asked to map.</summary>
internal sealed class CountingMapper : IFeedMapper
{
    private int calls;

    public IReadOnlyCollection<Type> EntityTypes { get; } = [typeof(Hold)];

    public int Calls => calls;

    public void Map(EntityEntry entry, FeedMapContext context) => Interlocked.Increment(ref calls);
}
