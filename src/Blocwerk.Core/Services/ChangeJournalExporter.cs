using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// Packages one or more journal batches into a portable <see cref="ChangeJournalPackage"/>: the batch
/// and entry rows in forward apply order, every byte[] blob they reference (deduplicated, Base64),
/// a base marker per distinct scope, plus the source instance and EF migration id used for the
/// schema-parity / divergence fail-fast on the replay side.
/// </summary>
public sealed class ChangeJournalExporter
{
    private readonly IDbContextFactory<BlocwerkDbContext> factory;

    public ChangeJournalExporter(IDbContextFactory<BlocwerkDbContext> factory)
    {
        this.factory = factory;
    }

    public async Task<ChangeJournalPackage> ExportBatchesAsync(
        IEnumerable<Guid> batchIds, CancellationToken cancellationToken = default)
    {
        var ids = batchIds.Distinct().ToList();

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.CurrentUserId = Guid.Empty;

        var batches = await db.ChangeJournalBatches
            .Where(b => ids.Contains(b.Id))
            .OrderBy(b => b.CreatedAt)
            .ToListAsync(cancellationToken);

        var order = batches.Select((b, i) => (b.Id, i)).ToDictionary(x => x.Id, x => x.i);

        var entries = await db.ChangeJournalEntries
            .Where(e => ids.Contains(e.BatchId))
            .ToListAsync(cancellationToken);

        // Total apply order: batch creation order, then Seq within the batch.
        entries = entries
            .OrderBy(e => order.TryGetValue(e.BatchId, out var i) ? i : int.MaxValue)
            .ThenBy(e => e.Seq)
            .ToList();

        var blobs = await LoadBlobsAsync(db, entries, cancellationToken);
        var markers = await BuildMarkersAsync(db, batches, cancellationToken);

        return new ChangeJournalPackage
        {
            SourceInstanceId = batches.Select(b => b.SourceInstanceId).FirstOrDefault(s => s is not null)
                ?? Environment.MachineName,
            MigrationId = await ChangeJournalMarkers.MigrationIdAsync(db, cancellationToken),
            Batches = batches.Select(ToPackageBatch).ToList(),
            Entries = entries.Select(ToPackageEntry).ToList(),
            Blobs = blobs,
            BaseMarkers = markers,
        };
    }

    private static async Task<List<ChangeJournalPackageBlob>> LoadBlobsAsync(
        BlocwerkDbContext db, IEnumerable<ChangeJournalEntry> entries, CancellationToken ct)
    {
        var shas = entries
            .SelectMany(e => ChangeJournalValueWriter.BlobShas(e.BeforeJson)
                .Concat(ChangeJournalValueWriter.BlobShas(e.AfterJson)))
            .Distinct()
            .ToList();

        if (shas.Count == 0)
        {
            return [];
        }

        var rows = await db.JournalBlobs.Where(b => shas.Contains(b.Sha256)).ToListAsync(ct);
        return rows
            .Select(b => new ChangeJournalPackageBlob
            {
                Sha256 = b.Sha256,
                Base64 = Convert.ToBase64String(b.Bytes),
                Len = b.Len,
            })
            .ToList();
    }

    private static async Task<List<ChangeJournalPackageBaseMarker>> BuildMarkersAsync(
        BlocwerkDbContext db, IEnumerable<ChangeJournalBatch> batches, CancellationToken ct)
    {
        var scopes = batches
            .Where(b => b.ScopeId is not null)
            .Select(b => (b.ScopeKind, b.ScopeId))
            .Distinct()
            .ToList();

        var markers = new List<ChangeJournalPackageBaseMarker>(scopes.Count);
        foreach (var (kind, id) in scopes)
        {
            markers.Add(await ChangeJournalMarkers.ComputeAsync(db, kind, id, ct));
        }

        return markers;
    }

    private static ChangeJournalPackageBatch ToPackageBatch(ChangeJournalBatch b) => new()
    {
        Id = b.Id,
        Label = b.Label,
        ScopeKind = b.ScopeKind,
        ScopeId = b.ScopeId,
        Actor = b.Actor,
        CreatedAt = b.CreatedAt,
    };

    private static ChangeJournalPackageEntry ToPackageEntry(ChangeJournalEntry e) => new()
    {
        BatchId = e.BatchId,
        Seq = e.Seq,
        EntityType = e.EntityType,
        KeyJson = e.KeyJson,
        Op = e.Op,
        BeforeJson = e.BeforeJson,
        AfterJson = e.AfterJson,
    };
}
