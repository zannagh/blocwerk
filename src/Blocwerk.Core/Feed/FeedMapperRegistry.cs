// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Feed;

/// <summary>The mappers by entity type (the families of <see cref="FeedMappers"/>).</summary>
public sealed class FeedMapperRegistry
{
    private readonly Dictionary<Type, IFeedMapper[]> byType;

    public FeedMapperRegistry(IEnumerable<IFeedMapper> mappers)
    {
        byType = mappers
            .SelectMany(m => m.EntityTypes.Select(t => (Type: t, Mapper: m)))
            .GroupBy(p => p.Type)
            .ToDictionary(g => g.Key, g => g.Select(p => p.Mapper).ToArray());
    }

    /// <summary>Gets a value indicating whether no mapper is registered, so a save has nothing to map.</summary>
    public bool IsEmpty => byType.Count == 0;

    /// <summary>Gets every mapped entity type.</summary>
    public IReadOnlyCollection<Type> MappedTypes => byType.Keys;

    /// <summary>The mappers of <paramref name="entityType"/>, or empty.</summary>
    public IReadOnlyList<IFeedMapper> For(Type entityType) => byType.TryGetValue(entityType, out var mappers) ? mappers : [];
}
