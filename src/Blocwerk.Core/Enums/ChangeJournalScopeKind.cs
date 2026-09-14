namespace Blocwerk.Core.Enums;

/// <summary>
/// What a <see cref="Entities.ChangeJournalBatch"/> is scoped to, so a later revert/replay can be
/// narrowed to a single aggregate rather than the whole log.
/// </summary>
public enum ChangeJournalScopeKind
{
    /// <summary>No particular aggregate — an ad-hoc, single-SaveChanges batch.</summary>
    None = 0,

    /// <summary>The batch belongs to one wall aggregate (its <see cref="Entities.ChangeJournalBatch.ScopeId"/> is a wall id).</summary>
    Wall = 1,

    /// <summary>The batch belongs to one boulder (its <see cref="Entities.ChangeJournalBatch.ScopeId"/> is a boulder id).</summary>
    Boulder = 2,
}
