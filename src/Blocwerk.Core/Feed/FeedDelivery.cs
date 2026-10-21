// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;

namespace Blocwerk.Core.Feed;

/// <summary>One batch the hub hands a subscriber: the wall's new events it may see, in feed order, or a resync.</summary>
/// <param name="WallId">The wall.</param>
/// <param name="Events">The events, already narrowed to the subscriber's audiences; empty on a resync.</param>
/// <param name="Cursor">Where the hub's read ended; the subscriber has seen everything of its audiences up to here.</param>
/// <param name="Resync">True when rows may be missing (another epoch, retention passed): reload the wall.</param>
public sealed record FeedDelivery(Guid WallId, IReadOnlyList<FeedEnvelope> Events, FeedCursor Cursor, bool Resync);
