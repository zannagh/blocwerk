// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Feed;

/// <summary>A post-commit nudge: <paramref name="WallId"/> has new rows, written by transaction <paramref name="TxId"/>.</summary>
/// <param name="WallId">The wall with new rows.</param>
/// <param name="TxId">The writing transaction (null on SQLite); the hub re-polls until it is below the horizon.</param>
public readonly record struct FeedWake(Guid WallId, long? TxId);
