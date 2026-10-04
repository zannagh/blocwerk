using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// <see cref="IHoldDuplicateService"/>. The detection is pure geometry (<see cref="HoldDuplicateFinder"/>) over each live
/// photo's holds; the merge is in the Merge part and the review picture in the Crop part.
/// </summary>
public sealed partial class HoldDuplicateService : IHoldDuplicateService
{
    /// <summary>The journal batch label of one merge.</summary>
    public const string BatchLabel = "hold-duplicate-merge";

    private const int MaxRecentMerges = 5;
    private const int MaxBoulderNames = 4;

    private readonly IDbContextFactory<BlocwerkDbContext> dbContextFactory;
    private readonly ICurrentUserService currentUserService;
    private readonly IChangeJournal journal;
    private readonly ChangeJournalReverter reverter;
    private readonly ILogger<HoldDuplicateService> logger;
    private readonly IKioskContext? kioskContext;

    /// <summary>Initializes a new instance of the <see cref="HoldDuplicateService"/> class.</summary>
    /// <param name="dbContextFactory">Context factory.</param>
    /// <param name="currentUserService">The acting user.</param>
    /// <param name="journal">Opens the named journal batch.</param>
    /// <param name="reverter">Reverts a batch.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="kioskContext">The kiosk context, when the host has one.</param>
    public HoldDuplicateService(
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        ICurrentUserService currentUserService,
        IChangeJournal journal,
        ChangeJournalReverter reverter,
        ILogger<HoldDuplicateService> logger,
        IKioskContext? kioskContext = null)
    {
        this.dbContextFactory = dbContextFactory;
        this.currentUserService = currentUserService;
        this.journal = journal;
        this.reverter = reverter;
        this.logger = logger;
        this.kioskContext = kioskContext;
    }

    /// <inheritdoc/>
    public async Task<HoldDuplicatePage> ListAsync(Guid wallId, int skip, int take, CancellationToken ct = default)
    {
        var (db, _) = await OpenForAdminAsync(wallId, ct);
        await using (db)
        {
            var holds = new Dictionary<Guid, Hold>();
            var candidates = new List<HoldDuplicateCandidate>();
            var counts = await ActiveBoulderCountsAsync(db, wallId, ct);
            foreach (var photo in await HoldOutlineUpgradeService.LoadLivePhotosAsync(db, wallId, ct))
            {
                var panel = await HoldOutlineUpgradeService.LiveHolds(db, wallId, photo).AsNoTracking().ToListAsync(ct);
                panel.ForEach(h => holds[h.Id] = h);
                candidates.AddRange(HoldDuplicateFinder.Find(panel, counts));
            }

            var dismissed = await DismissedAsync(db, wallId, ct);
            var open = candidates.Where(c => !dismissed.Contains(c.Key)).OrderByDescending(c => c.Confidence).ToList();
            var items = await ToItemsAsync(db, wallId, open.Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 100)).ToList(), holds, ct);
            var byKind = Enum.GetValues<HoldDuplicateKind>().ToDictionary(k => k, k => open.Count(c => c.Kind == k));
            return new HoldDuplicatePage(open.Count, byKind, items, await RecentMergesAsync(db, wallId, ct));
        }
    }

    /// <inheritdoc/>
    public async Task DismissAsync(Guid wallId, Guid holdAId, Guid holdBId, CancellationToken ct = default)
    {
        var (db, userId) = await OpenForAdminAsync(wallId, ct);
        await using (db)
        {
            var (a, b) = HoldMergeRules.PairKey(holdAId, holdBId);
            if (await db.HoldDuplicateDismissals.AnyAsync(d => d.WallId == wallId && d.HoldAId == a && d.HoldBId == b, ct))
            {
                return;
            }

            if (await db.Holds.CountAsync(h => h.WallId == wallId && (h.Id == a || h.Id == b), ct) != 2)
            {
                throw new UserFacingException("One of these holds no longer exists. Reload the list.");
            }

            db.HoldDuplicateDismissals.Add(new HoldDuplicateDismissal { WallId = wallId, HoldAId = a, HoldBId = b, DismissedByUserId = userId });
            await db.SaveChangesAsync(ct);
        }
    }

    /// <inheritdoc/>
    public async Task<ChangeJournalRevertResult> RevertAsync(Guid wallId, Guid batchId, CancellationToken ct = default)
    {
        var (db, _) = await OpenForAdminAsync(wallId, ct);
        await using (db)
        {
            var known = await db.ChangeJournalBatches.AsNoTracking()
                .AnyAsync(b => b.Id == batchId && b.Label == BatchLabel && b.ScopeId == wallId, ct);
            if (!known)
            {
                throw new UserFacingException("That merge was not found on this wall.");
            }
        }

        using var wallLock = AcquireWallLock(wallId);
        return await reverter.RevertBatchAsync(batchId, force: false, ct);
    }

    private static IDisposable AcquireWallLock(Guid wallId) =>
        WallHoldWriteLock.TryAcquire(wallId, "Another update of this wall's holds (a clean-up, an outline upgrade, a merge or a wall update) is running. Try again in a moment.");

    private static async Task<Dictionary<Guid, int>> ActiveBoulderCountsAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct) =>
        await db.BoulderHolds.AsNoTracking()
            .Where(bh => bh.Hold.WallId == wallId && !bh.Boulder.IsArchived && !bh.Boulder.IsHistoric)
            .GroupBy(bh => bh.HoldId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);

    private static async Task<HashSet<(Guid, Guid)>> DismissedAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct) =>
        (await db.HoldDuplicateDismissals.AsNoTracking().Where(d => d.WallId == wallId)
            .Select(d => new { d.HoldAId, d.HoldBId }).ToListAsync(ct))
        .Select(d => (d.HoldAId, d.HoldBId)).ToHashSet();

    private static async Task<List<HoldMergeRecord>> RecentMergesAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct)
    {
        // Few rows per wall; ordered in memory because SQLite cannot ORDER BY a DateTimeOffset.
        var batches = await db.ChangeJournalBatches.AsNoTracking()
            .Where(b => b.Label == BatchLabel && b.ScopeId == wallId && b.Status == ChangeJournalStatus.Recorded)
            .ToListAsync(ct);
        return [.. batches.OrderByDescending(b => b.CreatedAt).Take(MaxRecentMerges).Select(b => new HoldMergeRecord(b.Id, b.CreatedAt))];
    }

    private static async Task<List<HoldDuplicateItem>> ToItemsAsync(
        BlocwerkDbContext db, Guid wallId, List<HoldDuplicateCandidate> page, Dictionary<Guid, Hold> holds, CancellationToken ct)
    {
        var ids = page.SelectMany(c => new[] { c.HoldA, c.HoldB }).Distinct().ToList();
        var links = await db.BoulderHolds.AsNoTracking()
            .Where(bh => ids.Contains(bh.HoldId) && !bh.Boulder.IsArchived && !bh.Boulder.IsHistoric)
            .Select(bh => new { bh.HoldId, bh.BoulderId, bh.Boulder.Name })
            .ToListAsync(ct);
        HoldDuplicateHoldInfo Info(Guid id)
        {
            var mine = links.Where(l => l.HoldId == id).OrderBy(l => l.Name).ToList();
            var h = holds[id];
            return new HoldDuplicateHoldInfo(
                id, h.Name, h.Color, HoldMergeRules.IsHandMade(h), h.IsVirtual, mine.Count, [.. mine.Take(MaxBoulderNames).Select(l => l.Name)]);
        }

        return [.. page.Select(c => new HoldDuplicateItem(
            c.Kind,
            c.Confidence,
            Info(c.HoldA),
            Info(c.HoldB),
            c.SuggestedMode,
            links.Where(l => l.HoldId == c.HoldA || l.HoldId == c.HoldB).Select(l => l.BoulderId).Distinct().Count()))];
    }

    private async Task<(BlocwerkDbContext Db, Guid UserId)> OpenForAdminAsync(Guid wallId, CancellationToken ct)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        var db = await dbContextFactory.CreateDbContextAsync(ct);
        try
        {
            db.CurrentUserId = user.Id;
            KioskGuard.EnsureNotKiosk(kioskContext, db, "Reviewing duplicate holds");
            await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, ct);
            return (db, user.Id);
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }
}
