using Blocwerk.Core.Data;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// The read-only face of the change journal: turns "batch 4f2a…" into a line an operator can judge —
/// <c>hold-clean-outside-border — Nordwand — 181 Hold deleted — by Patrick — today 14:02</c>. Never
/// writes, and never materialises journal ENTRIES: counts come from a GROUP BY over the indexed
/// <c>BatchId</c>, so a batch that deleted 181 holds costs the same to list as one that renamed a wall.
/// <para>
/// Authorisation is the caller's job. Wall/boulder names are resolved with the membership query filter
/// ignored, because this is an admin view of the whole instance; only mount it behind an admin gate.
/// </para>
/// </summary>
public sealed class ChangeJournalBrowser
{
    private const int MaxPageSize = 200;

    private readonly IDbContextFactory<BlocwerkDbContext> factory;

    public ChangeJournalBrowser(IDbContextFactory<BlocwerkDbContext> factory)
    {
        this.factory = factory;
    }

    /// <summary>Most recent batches across the whole instance, newest first.</summary>
    public Task<ChangeJournalBatchPage> ListRecentAsync(
        int skip = 0, int take = 25, CancellationToken cancellationToken = default) =>
        ListAsync(null, null, skip, take, cancellationToken);

    /// <summary>
    /// Most recent batches on one aggregate, newest first — the wall history view. Uses the
    /// <c>IX_ChangeJournalBatches_ScopeKind_ScopeId</c> index.
    /// </summary>
    public Task<ChangeJournalBatchPage> ListForScopeAsync(
        ChangeJournalScopeKind scopeKind, Guid scopeId,
        int skip = 0, int take = 25, CancellationToken cancellationToken = default) =>
        ListAsync(scopeKind, scopeId, skip, take, cancellationToken);

    /// <summary>One batch's summary, or null when no batch has that id.</summary>
    public async Task<ChangeJournalBatchSummary?> GetBatchAsync(
        Guid batchId, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var rows = await ProjectAsync(db.ChangeJournalBatches.Where(b => b.Id == batchId), db, 0, 1, cancellationToken);
        var summaries = await ComposeAsync(db, rows, cancellationToken);
        return summaries.Count == 0 ? null : summaries[0];
    }

    private async Task<ChangeJournalBatchPage> ListAsync(
        ChangeJournalScopeKind? scopeKind, Guid? scopeId, int skip, int take, CancellationToken ct)
    {
        skip = Math.Max(skip, 0);
        take = Math.Clamp(take, 1, MaxPageSize);

        await using var db = await factory.CreateDbContextAsync(ct);
        var query = db.ChangeJournalBatches.AsQueryable();
        if (scopeKind is not null)
        {
            query = query.Where(b => b.ScopeKind == scopeKind && b.ScopeId == scopeId);
        }

        // One row over the page size is the cheap "is there a next page" probe; counting the whole
        // table would be the one query that degrades forever (there is no retention job).
        var rows = await ProjectAsync(query, db, skip, take + 1, ct);
        var hasMore = rows.Count > take;
        var page = hasMore ? rows.Take(take).ToList() : rows;
        var items = await ComposeAsync(db, page, ct);
        return new ChangeJournalBatchPage(items, skip, take, hasMore);
    }

    /// <summary>
    /// Reads the batch scalars plus, as a correlated subquery, how many batches on the same scope are
    /// newer. An unscoped batch cannot match itself (<c>ScopeKind != None</c> and
    /// <c>ScopeKind == b.ScopeKind</c> are contradictory there), so it counts 0 without a CASE.
    /// </summary>
    private static async Task<List<ChangeJournalBatchRow>> ProjectAsync(
        IQueryable<Entities.ChangeJournalBatch> query, BlocwerkDbContext db, int skip, int take, CancellationToken ct) =>
        await query
            .AsNoTracking()
            .OrderByDescending(b => b.CreatedAt)
            .ThenByDescending(b => b.Id)
            .Skip(skip)
            .Take(take)
            .Select(b => new ChangeJournalBatchRow(
                b.Id,
                b.Label,
                b.CreatedAt,
                b.SealedAt,
                b.Status,
                b.ScopeKind,
                b.ScopeId,
                b.Actor,
                db.ChangeJournalBatches.Count(o =>
                    o.ScopeKind != ChangeJournalScopeKind.None
                    && o.ScopeKind == b.ScopeKind
                    && o.ScopeId == b.ScopeId
                    && o.CreatedAt > b.CreatedAt)))
            .ToListAsync(ct);

    private static async Task<IReadOnlyList<ChangeJournalBatchSummary>> ComposeAsync(
        BlocwerkDbContext db, IReadOnlyList<ChangeJournalBatchRow> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var counts = await CountsAsync(db, rows.Select(r => r.Id).ToList(), ct);
        var scopeNames = await ScopeNamesAsync(db, rows, ct);
        var actorNames = await ActorNamesAsync(db, rows, ct);

        return rows
            .Select(r =>
            {
                var perBatch = counts.TryGetValue(r.Id, out var c) ? c : [];
                return new ChangeJournalBatchSummary(
                    r.Id,
                    r.Label,
                    r.CreatedAt,
                    r.SealedAt,
                    r.Status,
                    r.ScopeKind,
                    r.ScopeId,
                    r.ScopeId is not null && scopeNames.TryGetValue(r.ScopeId.Value, out var name) ? name : null,
                    r.Actor,
                    r.Actor is not null && actorNames.TryGetValue(r.Actor, out var actor) ? actor : null,
                    perBatch,
                    perBatch.Sum(x => x.Count),
                    r.NewerOnScope,
                    r.Label == ChangeJournal.WallUpdateBatchLabel && r.ScopeKind == ChangeJournalScopeKind.Wall);
            })
            .ToList();
    }

    /// <summary>GROUP BY BatchId, EntityType, Op over the indexed BatchId — no entry is ever materialised.</summary>
    private static async Task<Dictionary<Guid, IReadOnlyList<ChangeJournalEntityCount>>> CountsAsync(
        BlocwerkDbContext db, List<Guid> batchIds, CancellationToken ct)
    {
        var grouped = await db.ChangeJournalEntries
            .AsNoTracking()
            .Where(e => batchIds.Contains(e.BatchId))
            .GroupBy(e => new { e.BatchId, e.EntityType, e.Op })
            .Select(g => new { g.Key.BatchId, g.Key.EntityType, g.Key.Op, Count = g.Count() })
            .ToListAsync(ct);

        return grouped
            .GroupBy(g => g.BatchId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<ChangeJournalEntityCount>)g
                    .OrderByDescending(x => x.Count)
                    .ThenBy(x => x.EntityType)
                    .Select(x => new ChangeJournalEntityCount(x.EntityType, x.Op, x.Count))
                    .ToList());
    }

    private static async Task<Dictionary<Guid, string>> ScopeNamesAsync(
        BlocwerkDbContext db, IReadOnlyList<ChangeJournalBatchRow> rows, CancellationToken ct)
    {
        var names = new Dictionary<Guid, string>();
        var wallIds = ScopeIds(rows, ChangeJournalScopeKind.Wall);
        var boulderIds = ScopeIds(rows, ChangeJournalScopeKind.Boulder);

        if (wallIds.Count > 0)
        {
            // See the class note: an admin view resolves every wall's name, membership filter or not.
            var walls = await db.Walls.IgnoreQueryFilters().AsNoTracking()
                .Where(w => wallIds.Contains(w.Id)).Select(w => new { w.Id, w.Name }).ToListAsync(ct);
            foreach (var wall in walls)
            {
                names[wall.Id] = wall.Name;
            }
        }

        if (boulderIds.Count > 0)
        {
            var boulders = await db.Boulders.IgnoreQueryFilters().AsNoTracking()
                .Where(b => boulderIds.Contains(b.Id)).Select(b => new { b.Id, b.Name }).ToListAsync(ct);
            foreach (var boulder in boulders)
            {
                names[boulder.Id] = boulder.Name;
            }
        }

        return names;
    }

    /// <summary>
    /// Resolves the journalled <c>Actor</c> strings (user ids) to display names. A value that is not a
    /// guid, or names no user row, simply does not resolve — the summary then carries a null name.
    /// </summary>
    private static async Task<Dictionary<string, string>> ActorNamesAsync(
        BlocwerkDbContext db, IReadOnlyList<ChangeJournalBatchRow> rows, CancellationToken ct)
    {
        var ids = rows
            .Select(r => r.Actor)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => Guid.TryParse(a, out var id) ? id : (Guid?)null)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        if (ids.Count == 0)
        {
            return [];
        }

        // Name is a computed property (CustomDisplayName ?? DisplayName), so both columns come back and
        // the fallback is applied here rather than in SQL.
        var users = await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName, u.CustomDisplayName })
            .ToListAsync(ct);

        return users.ToDictionary(
            u => u.Id.ToString(),
            u => string.IsNullOrWhiteSpace(u.CustomDisplayName) ? u.DisplayName : u.CustomDisplayName);
    }

    private static List<Guid> ScopeIds(IReadOnlyList<ChangeJournalBatchRow> rows, ChangeJournalScopeKind kind) =>
        rows
            .Where(r => r.ScopeKind == kind && r.ScopeId is not null)
            .Select(r => r.ScopeId!.Value)
            .Distinct()
            .ToList();
}
