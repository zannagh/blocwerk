using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

/// <summary>
/// The scalar batch columns as read from the database, before names and counts are attached. Kept
/// separate from <see cref="ChangeJournalBatchSummary"/> so the SQL projection stays translatable and
/// pulls nothing it does not need.
/// </summary>
internal sealed record ChangeJournalBatchRow(
    Guid Id,
    string Label,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SealedAt,
    ChangeJournalStatus Status,
    ChangeJournalScopeKind ScopeKind,
    Guid? ScopeId,
    string? Actor,
    int NewerOnScope);
