using System.ComponentModel.DataAnnotations;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Entities;

/// <summary>
/// One logical unit of change in the mutation journal: either an explicit named batch (opened via
/// <see cref="Services.IChangeJournal.BeginBatch"/> and spanning one or more SaveChanges) or an
/// implicit ad-hoc batch created for a single SaveChanges that happened outside any scope. The
/// batch's <see cref="ChangeJournalEntry"/> rows hold the actual before/after row images, so a
/// batch can later be reverted or replayed as a whole.
/// </summary>
public class ChangeJournalBatch
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Human-readable label, e.g. a promote name or "adhoc" for an implicit batch.</summary>
    [MaxLength(200)]
    public string Label { get; set; } = string.Empty;

    /// <summary>What kind of aggregate <see cref="ScopeId"/> refers to, if any.</summary>
    public ChangeJournalScopeKind ScopeKind { get; set; } = ChangeJournalScopeKind.None;

    /// <summary>The scoped aggregate's id (wall/boulder), or null for an unscoped batch.</summary>
    public Guid? ScopeId { get; set; }

    /// <summary>Who caused the change, as an opaque string (the current user id, when known).</summary>
    [MaxLength(128)]
    public string? Actor { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// When the batch was closed and can no longer be appended to, or null while it is still OPEN. A
    /// wall update spans two calls (run then promote) that each SaveChanges on their own context; both
    /// resume the one OPEN batch for the wall (see <see cref="Services.IChangeJournal.BeginWallUpdateBatch"/>),
    /// and the promote (or a discard) seals it so the NEXT update starts a fresh batch. Ordinary
    /// single-call batches are created already effectively closed and never resumed.
    /// </summary>
    public DateTimeOffset? SealedAt { get; set; }

    /// <summary>The instance/environment this batch was recorded on, for cross-environment replay.</summary>
    [MaxLength(128)]
    public string? SourceInstanceId { get; set; }

    public ChangeJournalStatus Status { get; set; } = ChangeJournalStatus.Recorded;
}
