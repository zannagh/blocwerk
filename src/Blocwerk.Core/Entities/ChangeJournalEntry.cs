using System.ComponentModel.DataAnnotations;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Entities;

/// <summary>
/// A single row-level mutation inside a <see cref="ChangeJournalBatch"/>. Before/after images are
/// stored as JSON so any allow-listed entity shape can be recorded generically; byte[] columns are
/// not inlined but referenced by content hash into <see cref="JournalBlob"/>.
/// </summary>
public class ChangeJournalEntry
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid BatchId { get; set; }

    /// <summary>Order within the batch, assigned in ChangeTracker order across all its SaveChanges.</summary>
    public int Seq { get; set; }

    /// <summary>The CLR type name of the mutated entity (e.g. "Hold", "Wall").</summary>
    [MaxLength(200)]
    public string EntityType { get; set; } = string.Empty;

    /// <summary>
    /// The primary key as JSON, e.g. <c>{"Id":"…"}</c> or a composite <c>{"BoulderId":"…","HoldId":"…"}</c>.
    /// </summary>
    public string KeyJson { get; set; } = string.Empty;

    public ChangeJournalOp Op { get; set; }

    /// <summary>The pre-change values (null for an Insert). For an Update, only the changed properties.</summary>
    public string? BeforeJson { get; set; }

    /// <summary>The post-change values (null for a Delete). For an Update, only the changed properties.</summary>
    public string? AfterJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
