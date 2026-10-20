// <copyright file="WallEnhancementService.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Enhancement;

/// <summary>
/// <see cref="IWallEnhancementService"/>. Gate: <see cref="WallAdminGuard"/> plus <see cref="KioskGuard"/> on every call;
/// a share-link visitor has no signed-in user and is refused by the user lookup. The steps run in
/// <see cref="WallEnhancementRunner"/>; the review answers go through the app's own services as the reviewing admin.
/// </summary>
public sealed partial class WallEnhancementService(
    IDbContextFactory<BlocwerkDbContext> dbContextFactory,
    ICurrentUserService currentUserService,
    WallEnhancementRunner runner,
    IChangeJournal journal,
    ChangeJournalReverter reverter,
    IHoldDuplicateService duplicates,
    IHoldLinkSuggestionService links,
    IHoldProposalService proposals,
    ILogger<WallEnhancementService> logger,
    IKioskContext? kioskContext = null,
    IWallEditPresence? editPresence = null) : IWallEnhancementService
{
    private const string KioskRefusal = "Enhancing a wall";

    /// <inheritdoc/>
    public async Task<WallEnhancementView> GetAsync(Guid wallId, CancellationToken ct = default)
    {
        var (db, _) = await OpenForAdminAsync(wallId, ct);
        await using (db)
        {
            return await BuildViewAsync(db, wallId, ct);
        }
    }

    /// <inheritdoc/>
    public async Task<WallEnhancementView> StartAsync(Guid wallId, CancellationToken ct = default)
    {
        var (db, userId) = await OpenForAdminAsync(wallId, ct);
        await using (db)
        {
            var generation = await GenerationAsync(db, wallId, ct);
            var run = await db.WallEnhancementRuns.FirstOrDefaultAsync(r => r.WallId == wallId && r.Generation == generation, ct);
            switch (run?.Status)
            {
                case WallEnhancementStatus.Finalized:
                    throw new UserFacingException(
                        $"This wall was already enhanced for its current state on {run.FinalizedAt?.ToString("d MMM yyyy") ?? "an earlier day"}.");
                case WallEnhancementStatus.Review:
                    throw new UserFacingException("The enhancement already ran. Review its changes and finalize it.");
                case WallEnhancementStatus.Running:
                    runner.Launch(run.Id);
                    return await BuildViewAsync(db, wallId, ct);
            }

            if (await BlockedReasonAsync(db, wallId, ct) is { } blocked)
            {
                throw new UserFacingException(blocked);
            }

            run = run is null ? await CreateRunAsync(db, wallId, generation, userId, ct) : await ContinueAsync(db, run, ct);
            logger.LogInformation("Wall enhancement {RunId} of wall {WallId} generation {Generation} started by {UserId}", run.Id, wallId, generation, userId);
            runner.Launch(run.Id);
            return await BuildViewAsync(db, wallId, ct);
        }
    }

    private static async Task<WallEnhancementRun> CreateRunAsync(BlocwerkDbContext db, Guid wallId, int generation, Guid userId, CancellationToken ct)
    {
        var panels = await LiveWallHolds.LoadPanelIdsAsync(db, wallId, ct);
        var generations = await db.WallPanels.AsNoTracking()
            .Where(p => panels.Contains(p.Id))
            .Select(p => new { p.Id, p.Generation })
            .ToDictionaryAsync(p => p.Id.ToString(), p => p.Generation, ct);
        var run = new WallEnhancementRun
        {
            WallId = wallId, Generation = generation, CreatedByUserId = userId, PanelGenerationsJson = JsonSerializer.Serialize(generations),
        };
        db.WallEnhancementRuns.Add(run);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // The unique (wall, generation) index: another tap or admin started it a moment ago.
            throw new UserFacingException("The enhancement of this wall was just started. Its progress is shown here.");
        }

        return run;
    }

    private static async Task<WallEnhancementRun> ContinueAsync(BlocwerkDbContext db, WallEnhancementRun run, CancellationToken ct)
    {
        run.Status = WallEnhancementStatus.Running;
        run.Error = null;
        run.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return run;
    }

    /// <summary>Why the enhancement cannot start now: a wall update is open (it will produce a new generation anyway).</summary>
    private static async Task<string?> BlockedReasonAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct)
    {
        if (await db.WallUpdateSessions.AnyAsync(s => s.WallId == wallId && s.Status == WallUpdateSessionStatus.Open, ct))
        {
            return "A wall update is in progress. Finish or discard it first: the enhancement works on the wall as it is now.";
        }

        var open = new[] { WallRefreshStatus.Uploading, WallRefreshStatus.Sorting, WallRefreshStatus.ReadyToStart, WallRefreshStatus.Running, WallRefreshStatus.ReadyToApply, WallRefreshStatus.Applying };
        return await db.WallRefreshes.AnyAsync(r => r.WallId == wallId && open.Contains(r.Status), ct)
            ? "An \"Update panels + 3D\" is in progress. Finish or discard it first: the enhancement works on the wall as it is now."
            : null;
    }

    private static async Task<int> GenerationAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct) =>
        await db.Walls.Where(w => w.Id == wallId).Select(w => (int?)w.CurrentGeneration).FirstOrDefaultAsync(ct)
        ?? throw new UserFacingException("This wall no longer exists.");

    /// <summary>Opens a context for a wall admin, refused outright from any kiosk tablet.</summary>
    private async Task<(BlocwerkDbContext Db, Guid UserId)> OpenForAdminAsync(Guid wallId, CancellationToken ct)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        var db = await dbContextFactory.CreateDbContextAsync(ct);
        try
        {
            db.CurrentUserId = user.Id;
            KioskGuard.EnsureNotKiosk(kioskContext, db, KioskRefusal);
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
