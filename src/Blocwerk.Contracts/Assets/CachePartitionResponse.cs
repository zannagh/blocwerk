// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Contracts.Assets;

/// <summary>The body of <c>GET /api/me/cache-partition</c>.</summary>
/// <param name="Partition">The caller's partition (<see cref="CachePartitions"/>).</param>
public sealed record CachePartitionResponse(string Partition);
