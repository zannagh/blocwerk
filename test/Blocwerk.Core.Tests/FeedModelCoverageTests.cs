// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Feed;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The model-coverage guard (design §5.3): every entity of the EF model that belongs to a wall or a boulder (a
/// <c>WallId</c>/<c>BoulderId</c> property or a foreign key to <see cref="Wall"/>/<see cref="Boulder"/>) is mapped by a
/// production feed mapper, reviewed-excluded, or pending with its PR, and exactly one of them.
/// </summary>
public sealed class FeedModelCoverageTests
{
    [Fact]
    public void EveryWallOrBoulderEntity_IsMappedExcludedOrPending()
    {
        var required = WallScopedEntityTypes();
        var mapped = new FeedMapperRegistry(FeedMappers.Create()).MappedTypes.ToHashSet();
        var excluded = FeedCoverage.ReviewedExcluded.Keys.ToHashSet();
        var pending = FeedCoverage.Pending.Keys.ToHashSet();

        var unclassified = required.Where(t => !mapped.Contains(t) && !excluded.Contains(t) && !pending.Contains(t))
            .Select(t => t.Name).Order().ToList();
        Assert.True(unclassified.Count == 0, "Classify for the feed (map it, or list it in FeedCoverage): " + string.Join(", ", unclassified));
    }

    [Fact]
    public void NoEntityIsClassifiedTwice_AndNoListedTypeIsStale()
    {
        var required = WallScopedEntityTypes();
        var mapped = new FeedMapperRegistry(FeedMappers.Create()).MappedTypes.ToHashSet();
        var lists = new[] { mapped, FeedCoverage.ReviewedExcluded.Keys.ToHashSet(), FeedCoverage.Pending.Keys.ToHashSet() };

        var twice = lists.SelectMany(l => l).GroupBy(t => t).Where(g => g.Count() > 1).Select(g => g.Key.Name).ToList();
        var stale = FeedCoverage.ReviewedExcluded.Keys.Concat(FeedCoverage.Pending.Keys)
            .Where(t => !required.Contains(t)).Select(t => t.Name).ToList();

        Assert.True(twice.Count == 0, "Classified more than once: " + string.Join(", ", twice));
        Assert.True(stale.Count == 0, "Listed but not wall-scoped (remove the entry): " + string.Join(", ", stale));
    }

    [Fact]
    public void EveryExclusionAndPendingEntry_SaysWhy()
    {
        Assert.All(FeedCoverage.ReviewedExcluded, e => Assert.False(string.IsNullOrWhiteSpace(e.Value), e.Key.Name));
        Assert.All(FeedCoverage.Pending, e => Assert.Matches("^1\\.2[bc]: ", e.Value));
    }

    private static HashSet<Type> WallScopedEntityTypes()
    {
        using var db = TestDb.Create("Host=unused;Database=unused");
        var types = db.Model.GetEntityTypes()
            .Where(t => !t.IsOwned() && t.ClrType != typeof(WallChange))
            .Where(IsWallScoped)
            .Select(t => t.ClrType)
            .ToHashSet();
        types.Add(typeof(Wall));
        return types;
    }

    private static bool IsWallScoped(IEntityType type) =>
        type.FindProperty("WallId") is not null
        || type.FindProperty("BoulderId") is not null
        || type.GetForeignKeys().Any(fk => fk.PrincipalEntityType.ClrType == typeof(Wall) || fk.PrincipalEntityType.ClrType == typeof(Boulder));
}
