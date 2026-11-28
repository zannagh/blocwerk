// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Blocwerk.Core.Entities;

/// <summary>
/// The replay key of one companion-app write that creates a row the entity has no client request id of its own for (a
/// gallery photo, a beta video, a boulder): the app's <see cref="ClientRequestId"/>, unique per <see cref="UserId"/>,
/// and the row it made. A retry of the same write finds this receipt and gets the stored row back instead of a second one.
/// </summary>
/// <remarks>
/// A receipt is taken before the write starts (<see cref="SubjectId"/> still null) and completed after it, so two
/// concurrent retries never both write: the unique index lets exactly one of them in. A write that fails drops its
/// receipt again. Gone with the user.
/// </remarks>
public class DeviceWriteReceipt
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The user whose write it is.</summary>
    public Guid UserId { get; set; }

    [ForeignKey(nameof(UserId))]
    public User User { get; set; } = null!;

    /// <summary>The app's replay key for the write; never the empty id.</summary>
    public Guid ClientRequestId { get; set; }

    /// <summary>What the write makes, e.g. <c>gallery</c>, <c>beta</c>, <c>boulder</c>; a key reused for another kind is refused.</summary>
    [Required]
    [MaxLength(32)]
    public required string Kind { get; set; }

    /// <summary>The wall or boulder the write was for; a key reused for another one is refused.</summary>
    public Guid ScopeId { get; set; }

    /// <summary>The row the write made; null while the write is still running.</summary>
    public Guid? SubjectId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
