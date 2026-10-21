// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.ComponentModel.DataAnnotations;

namespace Blocwerk.Core.Entities;

/// <summary>
/// The feed's one-row epoch. Transaction ids are not stable across a dump and restore (a restored cluster restarts its
/// xid counter), so the feed's cursors carry this id: a cursor of another epoch is answered with a resync. A new epoch
/// is written, and the feed truncated, when the stored transaction ids turn out to come from another cluster.
/// </summary>
public class FeedEpoch
{
    /// <summary>Gets or sets the epoch id that every cursor carries.</summary>
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Gets or sets the transaction id of the newest row retention deleted (null until it deletes one, and on SQLite).</summary>
    public long? FloorTxId { get; set; }

    /// <summary>Gets or sets the sequence of the newest row retention deleted. A cursor at or below the floor may have missed rows.</summary>
    public long FloorSeq { get; set; }
}
