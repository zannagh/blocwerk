// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Blocwerk.Core.Feed.Structure;

/// <summary>
/// The parts a boulder is shown with: its holds (<see cref="BoulderHold"/>), setters (<see cref="BoulderSetter"/>) and
/// move hints (<see cref="BoulderHoldMove"/>). They write no row of their own: each touches its boulder, and the save
/// writes one <c>boulder.changed</c> per boulder however many of its parts changed (<see cref="FeedBoulderResolver"/>).
/// The setter's user id never reaches the feed.
/// </summary>
internal sealed class BoulderPartFeedMapper : IFeedMapper
{
    public IReadOnlyCollection<Type> EntityTypes { get; } = [typeof(BoulderHold), typeof(BoulderSetter), typeof(BoulderHoldMove)];

    public void Map(EntityEntry entry, FeedMapContext context)
    {
        switch (entry.Entity)
        {
            case BoulderHold part:
                context.TouchBoulder(part.BoulderId, part.Boulder, part.Hold?.WallId);
                break;
            case BoulderSetter part:
                context.TouchBoulder(part.BoulderId, part.Boulder, null);
                break;
            case BoulderHoldMove part:
                context.TouchBoulder(part.BoulderId, part.Boulder, part.WallId);
                break;
        }
    }
}
