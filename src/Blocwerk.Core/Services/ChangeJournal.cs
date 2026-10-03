using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// Ambient batch tracker for the change journal. A singleton: the "current batch" is held in an
/// <see cref="AsyncLocal{T}"/>, so it flows with the async call chain and is isolated between
/// concurrent requests/circuits without any per-request state on the service itself.
/// </summary>
public sealed partial class ChangeJournal : IChangeJournal
{
    /// <summary>The label the run→promote wall update shares, so both calls resume the same batch.</summary>
    public const string WallUpdateBatchLabel = "wall-update";

    private readonly AsyncLocal<ChangeJournalBatchScope?> current = new();
    private readonly Func<BlocwerkDbContext>? registryContextFactory;

    /// <param name="registryContextFactory">
    /// Creates a context used ONLY to read/write the batch registry itself (find-or-create the open
    /// wall-update batch, seal it). Null for callers that never open a wall-update batch (unit tests
    /// that only use <see cref="BeginBatch"/>); <see cref="BeginWallUpdateBatch"/> then throws.
    /// </param>
    public ChangeJournal(Func<BlocwerkDbContext>? registryContextFactory = null)
    {
        this.registryContextFactory = registryContextFactory;
    }

    /// <summary>The batch open on the current async flow, or null when none is.</summary>
    internal ChangeJournalBatchScope? Current => current.Value;

    public IDisposable BeginBatch(
        string label,
        ChangeJournalScopeKind scopeKind = ChangeJournalScopeKind.None,
        Guid? scopeId = null)
    {
        return Enter(label, scopeKind, scopeId, batchId: null, startSeq: 0, batchRowCreated: false);
    }

    /// <inheritdoc/>
    public IDisposable BeginWallUpdateBatch(Guid wallId)
    {
        // Find-or-create the wall's one OPEN wall-update batch. Persisting the created row here (rather
        // than lazily on the first journalled write, as BeginBatch does) is what lets the run and the
        // later promote — different contexts — resolve the SAME batch id. Race note: two concurrent
        // updates on ONE wall could each create a batch; in practice a wall is updated by one admin at a
        // time (the start/promote flow is a single serialised UI session), so the simple query+insert is
        // enough and no unique constraint is added.
        using var db = RequireRegistryContext();
        var open = db.ChangeJournalBatches.FirstOrDefault(b =>
            b.ScopeKind == ChangeJournalScopeKind.Wall
            && b.ScopeId == wallId
            && b.Label == WallUpdateBatchLabel
            && b.SealedAt == null);

        Guid batchId;
        int startSeq;
        if (open is not null)
        {
            batchId = open.Id;

            // Continue Seq from the batch's existing entries so ordering is monotonic across the run's
            // staging writes and the promote's writes. Max over an empty set is null → start at 0.
            var maxSeq = db.ChangeJournalEntries
                .Where(e => e.BatchId == batchId)
                .Select(e => (int?)e.Seq)
                .Max();
            startSeq = (maxSeq ?? -1) + 1;
        }
        else
        {
            var batch = new ChangeJournalBatch
            {
                Label = WallUpdateBatchLabel,
                ScopeKind = ChangeJournalScopeKind.Wall,
                ScopeId = wallId,
                CreatedAt = DateTimeOffset.UtcNow,
                Status = ChangeJournalStatus.Recorded,
            };
            db.ChangeJournalBatches.Add(batch);
            db.SaveChanges();
            batchId = batch.Id;
            startSeq = 0;
        }

        return Enter(WallUpdateBatchLabel, ChangeJournalScopeKind.Wall, wallId, batchId, startSeq, batchRowCreated: true);
    }

    /// <inheritdoc/>
    public async Task SealWallUpdateBatchAsync(Guid wallId)
    {
        await using var db = RequireRegistryContext();
        var open = await db.ChangeJournalBatches.FirstOrDefaultAsync(b =>
            b.ScopeKind == ChangeJournalScopeKind.Wall
            && b.ScopeId == wallId
            && b.Label == WallUpdateBatchLabel
            && b.SealedAt == null);
        if (open is null)
        {
            return;
        }

        open.SealedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Makes a batch the ambient one on the current async flow (every scope opens through here); disposing the result
    /// restores the previous one. Synchronous on purpose: an <see cref="AsyncLocal{T}"/> set inside an async method
    /// would not flow back to its caller.
    /// </summary>
    private ChangeJournalBatchScope Enter(
        string label, ChangeJournalScopeKind scopeKind, Guid? scopeId, Guid? batchId, int startSeq, bool batchRowCreated)
    {
        var previous = current.Value;
        var scope = new ChangeJournalBatchScope(
            label, scopeKind, scopeId, () => current.Value = previous, batchId, startSeq, batchRowCreated);
        current.Value = scope;
        return scope;
    }

    private BlocwerkDbContext RequireRegistryContext()
    {
        if (registryContextFactory is null)
        {
            throw new InvalidOperationException(
                "ChangeJournal was constructed without a registry context factory; BeginWallUpdateBatch/"
                + "SealWallUpdateBatchAsync are unavailable.");
        }

        return registryContextFactory();
    }
}
