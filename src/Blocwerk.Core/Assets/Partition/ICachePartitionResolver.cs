// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Assets.Partition;

/// <summary>Resolves the current request's or circuit's cache partition (<see cref="CachePartitionResolver"/>).</summary>
public interface ICachePartitionResolver
{
    /// <summary>The partition label (<see cref="Blocwerk.Contracts.Assets.CachePartitions"/>). Never throws for an anonymous caller.</summary>
    Task<string> ResolveAsync();
}
