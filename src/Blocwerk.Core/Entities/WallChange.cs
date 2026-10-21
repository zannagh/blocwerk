// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.ComponentModel.DataAnnotations;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Entities;

/// <summary>
/// One row of the durable live-update feed (the outbox): a change a viewer may see, written in the same transaction as the
/// edit. It carries ids, never content (no names, comment text or notes): content is hydrated from the source tables at
/// read time, so erasing a user leaves nothing personal here. Rows are ordered by <c>(TxId, Seq)</c> and read only below
/// the Postgres snapshot horizon (see <see cref="Feed.IWallFeed"/>). There are no foreign keys: a row outlives nothing it
/// points at for long, and retention removes it after 30 days.
/// </summary>
public class WallChange
{
    /// <summary>Gets or sets the identity value; orders the rows inside one transaction.</summary>
    [Key]
    public long Seq { get; set; }

    /// <summary>
    /// Gets or sets the writing transaction's id (<c>pg_current_xact_id()</c>), set by a Postgres column default and read back
    /// on insert. Never set it. Null on SQLite, which has no such default.
    /// </summary>
    public long? TxId { get; set; }

    /// <summary>Gets or sets the wall, null only for wall-less <see cref="FeedAudience.User"/> rows.</summary>
    public Guid? WallId { get; set; }

    public Guid? BoulderId { get; set; }

    /// <summary>Gets or sets the kind, e.g. <c>comment.added</c>.</summary>
    [MaxLength(64)]
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets the changed row (comment id, hold id, …).</summary>
    public Guid? EntityId { get; set; }

    public FeedAudience Audience { get; set; } = FeedAudience.Wall;

    /// <summary>Gets or sets the one recipient of a <see cref="FeedAudience.User"/> row.</summary>
    public Guid? AudienceUserId { get; set; }

    /// <summary>Gets or sets who made the change: an id only, name and avatar are resolved at read time.</summary>
    public Guid? ActorUserId { get; set; }

    /// <summary>Gets or sets the non-personal payload (ids, enums, flags, generations), or null for coarse kinds.</summary>
    public string? DataJson { get; set; }

    /// <summary>Gets or sets the payload version.</summary>
    public short SchemaVersion { get; set; } = 1;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
