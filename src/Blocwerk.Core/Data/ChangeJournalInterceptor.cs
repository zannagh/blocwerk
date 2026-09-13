using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Blocwerk.Core.Data;

/// <summary>
/// Persists a before/after mutation log for allow-listed entities. Runs in the <c>SavingChanges</c>
/// pass — while the ChangeTracker still carries original values — and adds the journal rows to the
/// SAME <see cref="DbContext"/>, so they are written inside the very SaveChanges/transaction that
/// applies the effect: journal and effect commit or roll back together.
/// </summary>
/// <remarks>
/// Separate from <see cref="DomainChangeInterceptor"/>, which persists nothing and only broadcasts
/// cache-invalidation ids. Entries attach to the ambient <see cref="ChangeJournal"/> batch when one
/// is open, otherwise an implicit single-SaveChanges "adhoc" batch is created. The journal entity
/// types are deliberately absent from <see cref="Allowlist"/>, which is the recursion guard: the
/// interceptor's own inserts are never themselves journalled.
/// </remarks>
public sealed class ChangeJournalInterceptor : SaveChangesInterceptor
{
    /// <summary>
    /// The entity types whose row changes are captured. Deliberately small and explicit; everything
    /// else — the journal types, users, activity log, attempts, etc. — is not journalled.
    /// </summary>
    public static readonly HashSet<Type> Allowlist =
    [
        typeof(Wall),
        typeof(WallPanel),
        typeof(Hold),
        typeof(HoldGenerationLink),
        typeof(HoldLink),
        typeof(BoulderHold),
        typeof(Boulder),
        typeof(WallReset),
    ];

    private readonly ChangeJournal changeJournal;

    public ChangeJournalInterceptor(ChangeJournal changeJournal)
    {
        this.changeJournal = changeJournal;
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Capture(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        await CaptureAsync(eventData.Context, cancellationToken);
        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Capture(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var (entries, pendingBlobs) = Prepare(context);
        if (entries is null || pendingBlobs is null)
        {
            return;
        }

        context.AddRange(ChangeJournalValueWriter.FilterNewBlobs(context, pendingBlobs));
        context.AddRange(entries);
    }

    private async Task CaptureAsync(DbContext? context, CancellationToken cancellationToken)
    {
        if (context is null)
        {
            return;
        }

        var (entries, pendingBlobs) = Prepare(context);
        if (entries is null || pendingBlobs is null)
        {
            return;
        }

        // Async existence check for blob dedup, so the async save path never blocks on a sync query.
        var newBlobs = await ChangeJournalValueWriter.FilterNewBlobsAsync(context, pendingBlobs, cancellationToken);
        context.AddRange(newBlobs);
        context.AddRange(entries);
    }

    /// <summary>
    /// Builds the journal entries and candidate blobs for the current allow-listed changes without
    /// touching the store, so the sync/async callers only differ in how they run the blob-existence
    /// filter. Returns null (as null members) when nothing is journalled.
    /// </summary>
    private (List<ChangeJournalEntry>? Entries, List<JournalBlob>? PendingBlobs) Prepare(DbContext context)
    {
        // Snapshot the allow-listed changes BEFORE adding any journal rows, so the pass never sees
        // its own inserts and original values are still intact.
        var tracked = context.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Where(e => Allowlist.Contains(e.Metadata.ClrType))
            .ToList();

        if (tracked.Count == 0)
        {
            return (null, null);
        }

        var scope = changeJournal.Current;
        var now = DateTimeOffset.UtcNow;
        var batchId = scope?.BatchId ?? Guid.NewGuid();
        var seq = scope?.NextSeq ?? 0;
        var pendingBlobs = new List<JournalBlob>();
        var entries = new List<ChangeJournalEntry>();

        foreach (var entry in tracked)
        {
            entries.Add(BuildEntry(entry, batchId, seq++, now, pendingBlobs));
        }

        EnsureBatchRow(context, scope, batchId, now, ActorOf(context));
        if (scope is not null)
        {
            scope.NextSeq = seq;
        }

        return (entries, pendingBlobs);
    }

    private static ChangeJournalEntry BuildEntry(
        EntityEntry entry, Guid batchId, int seq, DateTimeOffset now, List<JournalBlob> pendingBlobs)
    {
        var op = entry.State switch
        {
            EntityState.Added => ChangeJournalOp.Insert,
            EntityState.Deleted => ChangeJournalOp.Delete,
            _ => ChangeJournalOp.Update,
        };

        string? before = null;
        string? after = null;
        switch (op)
        {
            case ChangeJournalOp.Insert:
                after = ChangeJournalValueWriter.Serialize(entry.Properties.ToList(), useCurrent: true, pendingBlobs);
                break;
            case ChangeJournalOp.Delete:
                before = ChangeJournalValueWriter.Serialize(entry.Properties.ToList(), useCurrent: false, pendingBlobs);
                break;
            default:
                var changed = entry.Properties.Where(p => p.IsModified).ToList();
                before = ChangeJournalValueWriter.Serialize(changed, useCurrent: false, pendingBlobs);
                after = ChangeJournalValueWriter.Serialize(changed, useCurrent: true, pendingBlobs);
                break;
        }

        return new ChangeJournalEntry
        {
            BatchId = batchId,
            Seq = seq,
            EntityType = entry.Metadata.ClrType.Name,
            KeyJson = ChangeJournalValueWriter.SerializeKey(entry),
            Op = op,
            BeforeJson = before,
            AfterJson = after,
            CreatedAt = now,
        };
    }

    private static void EnsureBatchRow(
        DbContext context, ChangeJournalBatchScope? scope, Guid batchId, DateTimeOffset now, string? actor)
    {
        if (scope is { BatchRowCreated: true })
        {
            return;
        }

        context.Add(new ChangeJournalBatch
        {
            Id = batchId,
            Label = scope?.Label ?? "adhoc",
            ScopeKind = scope?.ScopeKind ?? ChangeJournalScopeKind.None,
            ScopeId = scope?.ScopeId,
            Actor = actor,
            CreatedAt = now,
            Status = ChangeJournalStatus.Recorded,
        });

        if (scope is not null)
        {
            scope.BatchRowCreated = true;
        }
    }

    private static string? ActorOf(DbContext context)
    {
        return context is BlocwerkDbContext bwk && bwk.CurrentUserId != Guid.Empty
            ? bwk.CurrentUserId.ToString()
            : null;
    }
}
