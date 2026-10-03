using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

/// <summary>
/// Opens a named, ambient change-journal batch that every <c>SaveChanges</c> on the current async
/// flow attaches its entries to, until the returned scope is disposed. When no batch is open, the
/// journal interceptor records each SaveChanges as its own implicit ("adhoc") batch instead.
/// </summary>
public interface IChangeJournal
{
    /// <summary>
    /// Begins a batch on the current async flow. Nesting is supported: the previous batch is
    /// restored when the returned scope is disposed. The batch row itself is only persisted lazily,
    /// on the first SaveChanges that produces a journalled change, so empty scopes cost nothing.
    /// </summary>
    IDisposable BeginBatch(string label, ChangeJournalScopeKind scopeKind = ChangeJournalScopeKind.None, Guid? scopeId = null);

    /// <summary>
    /// Begins a batch like <see cref="BeginBatch"/> for one audited action (an automation API write). Unlike a plain
    /// batch, <see cref="ChangeJournalAction.CompleteAsync"/> persists the batch row even when the action changed no
    /// journalled entity (a marker plan, a wall update run), so every audited write leaves a row naming its label,
    /// scope and actor. Requires the registry context factory, like <see cref="BeginWallUpdateBatch"/>.
    /// </summary>
    ChangeJournalAction BeginAction(string label, ChangeJournalScopeKind scopeKind, Guid? scopeId);

    /// <summary>
    /// Begins (or resumes) the single OPEN wall-update batch for a wall, so a whole wall update — the
    /// "run" that stages panels and inserts the next-generation holds AND the later "promote" that
    /// carries them over — records into ONE batch even though the two run on separate contexts. Finds
    /// the wall's open (unsealed) <c>"wall-update"</c> batch, creating and persisting one if none
    /// exists, sets it as the ambient batch, and seeds the running <c>Seq</c> from the batch's existing
    /// entries so numbering continues across calls. Seal it with <see cref="SealWallUpdateBatchAsync"/>
    /// once the update is complete, so the next update opens a fresh batch. Requires the journal to have
    /// been constructed with a registry context factory (it is, in production); a no-arg instance throws.
    /// </summary>
    IDisposable BeginWallUpdateBatch(Guid wallId);

    /// <summary>
    /// Seals the wall's open <c>"wall-update"</c> batch (sets <see cref="Entities.ChangeJournalBatch.SealedAt"/>),
    /// closing it so a later <see cref="BeginWallUpdateBatch"/> starts a new one. A no-op when the wall
    /// has no open wall-update batch.
    /// </summary>
    Task SealWallUpdateBatchAsync(Guid wallId);
}
