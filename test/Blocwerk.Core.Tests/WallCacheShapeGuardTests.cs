// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Collections;
using System.Reflection;
using Blocwerk.Contracts.Feed;
using Blocwerk.Contracts.Feed.Payloads;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Web.State.Feed;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The cached read shapes and <see cref="WallFeedEviction"/> stay in sync (PR 1.8 review, W2). Every entity type reachable
/// from what the circuit caches (the wall aggregate, a boulder, a hold set) must be listed in
/// <see cref="WallFeedEviction.CachedShapes"/>, and every kind listed there must actually evict a warm entry. A cached read
/// that starts loading another entity type fails here until its eviction is decided.
/// </summary>
public sealed class WallCacheShapeGuardTests
{
    [Fact]
    public async Task EveryEntityTypeACachedReadHandsOut_IsListedWithItsEvictingKinds()
    {
        await using var f = new CircuitCacheFixture();
        await f.StartAsync();
        var member = await f.Harness.AddMemberAsync("setter@test", WallRole.Member);
        var boulder = await f.Harness.BoulderService.CreateBoulderAsync(
            f.WallId, "Shaped", "6A", [new BoulderHoldInput(f.Holds[0].Id, HoldType.Start)], setterUserIds: [member.Id]);

        await using var db = f.Harness.CreateContext();
        var reached = new HashSet<Type>();
        Walk(db.Model, await f.Harness.WallService.GetWallAsync(f.WallId), reached, new HashSet<object>(ReferenceEqualityComparer.Instance));
        Walk(db.Model, await f.Harness.BoulderService.GetBoulderAsync(boulder.Id), reached, new HashSet<object>(ReferenceEqualityComparer.Instance));
        Walk(db.Model, await f.Harness.WallService.GetHoldsForGenerationAsync(f.WallId, 0), reached, new HashSet<object>(ReferenceEqualityComparer.Instance));

        Assert.Contains(typeof(BoulderSetter), reached); // the walk sees the deep parts, not only the roots
        Assert.Empty(reached.Where(t => !WallFeedEviction.CachedShapes.ContainsKey(t)).Select(t => t.Name));
    }

    [Fact]
    public async Task EveryListedKind_EvictsAWarmEntry()
    {
        foreach (var kind in WallFeedEviction.CachedShapes.Values.SelectMany(k => k).Distinct())
        {
            await using var f = new CircuitCacheFixture();
            await f.StartAsync();
            var boulder = await f.AddBoulderAsync("B", f.Holds[0]);
            await f.WarmAsync(boulder);
            var entity = kind switch
            {
                FeedKinds.HoldChanged or FeedKinds.HoldDeleted => f.Holds[0].Id,
                FeedKinds.BoulderChanged or FeedKinds.BoulderDeleted => boulder.Id,
                FeedKinds.UserProfile => f.Harness.Owner.Id,
                _ => f.WallId,
            };
            FeedPayload? data = kind is FeedKinds.HoldChanged or FeedKinds.HoldDeleted ? new HoldPayload(0, null) : null;

            await f.DeliverAsync(f.Row(kind, entity, data));

            Assert.True(
                !f.Store.Walls.TryPeek(f.WallId, out _) || !f.Store.Boulders.TryPeek(boulder.Id, out _),
                $"{kind} evicted nothing");
        }
    }

    private static void Walk(IModel model, object? node, HashSet<Type> reached, HashSet<object> seen)
    {
        if (node is null || !seen.Add(node))
        {
            return;
        }

        if (node is IEnumerable items and not string)
        {
            foreach (var item in items)
            {
                Walk(model, item, reached, seen);
            }

            return;
        }

        if (model.FindEntityType(node.GetType()) is not { } entityType)
        {
            return;
        }

        if (!entityType.IsOwned())
        {
            reached.Add(node.GetType());
        }

        foreach (var navigation in entityType.GetNavigations().Cast<INavigationBase>().Concat(entityType.GetSkipNavigations()))
        {
            if (navigation.PropertyInfo is PropertyInfo property)
            {
                Walk(model, property.GetValue(node), reached, seen);
            }
        }
    }
}
