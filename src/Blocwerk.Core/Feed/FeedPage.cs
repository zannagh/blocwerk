// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Feed;

/// <summary>One horizon read.</summary>
/// <param name="Changes">The rows after the cursor, in <c>(TxId, Seq)</c> order; empty on a resync.</param>
/// <param name="Next">Where the next read continues. On a resync: the horizon before the reload the caller now has to do.</param>
/// <param name="Resync">True when the cursor is of another epoch or older than retention: rows may be missing, reload instead.</param>
public sealed record FeedPage(IReadOnlyList<WallChange> Changes, FeedCursor Next, bool Resync);
