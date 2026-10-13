// <copyright file="HoldStackService.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.HoldStacks;

/// <summary><see cref="IHoldStackService"/>: the plan is the pure rule over each live photo's holds, the apply one journal batch.</summary>
public sealed class HoldStackService : IHoldStackService
{
    /// <summary>The journal batch label of an applied run.</summary>
    public const string BatchLabel = "hold-stacks";

    private readonly IDbContextFactory<BlocwerkDbContext> dbContextFactory;
    private readonly ICurrentUserService currentUserService;
    private readonly ILogger<HoldStackService> logger;
    private readonly IChangeJournal? journal;
    private readonly IKioskContext? kioskContext;

    /// <summary>Initializes a new instance of the <see cref="HoldStackService"/> class.</summary>
    /// <param name="dbContextFactory">Context factory.</param>
    /// <param name="currentUserService">The acting user.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="journal">Opens the named journal batch, when the host has a journal.</param>
    /// <param name="kioskContext">The kiosk context, when the host has one.</param>
    public HoldStackService(
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        ICurrentUserService currentUserService,
        ILogger<HoldStackService> logger,
        IChangeJournal? journal = null,
        IKioskContext? kioskContext = null)
    {
        this.dbContextFactory = dbContextFactory;
        this.currentUserService = currentUserService;
        this.logger = logger;
        this.journal = journal;
        this.kioskContext = kioskContext;
    }

    /// <inheritdoc/>
    public async Task<HoldStackReport> PreviewAsync(Guid wallId, CancellationToken ct = default)
    {
        await using var db = await OpenForAdminAsync(wallId, ct);
        var live = await (await LiveWallHolds.QueryAsync(db, wallId, ct)).AsNoTracking().ToListAsync(ct);
        var plan = await HoldStacker.PlanAllAsync(db, live, ct);
        return Report(applied: false, live.Count, live.Count(h => h.StackedOnHoldId is not null), plan, null);
    }

    /// <inheritdoc/>
    public async Task<HoldStackReport> ApplyAsync(Guid wallId, IReadOnlyCollection<Guid>? holdIds = null, CancellationToken ct = default)
    {
        await using var db = await OpenForAdminAsync(wallId, ct);
        using var wallLock = WallHoldWriteLock.TryAcquire(
            wallId, "Another update of this wall's holds (a clean-up, an outline upgrade, a merge or a wall update) is running. Try again in a moment.");
        var live = await (await LiveWallHolds.QueryAsync(db, wallId, ct)).ToListAsync(ct);
        var before = live.Count(h => h.StackedOnHoldId is not null);
        var plan = (await HoldStacker.PlanAllAsync(db, live, ct)).Where(p => holdIds is null || holdIds.Contains(p.Upper.Id)).ToList();
        if (plan.Count == 0)
        {
            return Report(applied: true, live.Count, before, plan, null);
        }

        HoldStacker.Apply(plan);
        ct.ThrowIfCancellationRequested();
        using var scope = journal?.BeginBatch(BatchLabel, ChangeJournalScopeKind.Wall, wallId);
        await db.SaveChangesAsync(ct);
        var batchId = (scope as ChangeJournalBatchScope)?.BatchId;
        logger.LogInformation(
            "Hold stacks on wall {WallId} by {UserId}: {Stacked} of {Holds} live holds stacked on another hold, journal batch {BatchId}",
            wallId, db.CurrentUserId, plan.Count, live.Count, batchId);
        return Report(applied: true, live.Count, before, plan, batchId);
    }

    private static HoldStackReport Report(bool applied, int live, int before, List<HoldStackPlan> plan, Guid? batchId) => new(
        applied,
        live,
        before,
        [.. plan.Select(p => new HoldStackProposal(
            p.Upper.Id, p.Base.Id, p.Upper.WallPanelId, (int)Math.Round(100 * p.Inside), Math.Round(p.AreaRatio, 2), p.Upper.Color, p.Base.Color))],
        batchId);

    private async Task<BlocwerkDbContext> OpenForAdminAsync(Guid wallId, CancellationToken ct)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        var db = await dbContextFactory.CreateDbContextAsync(ct);
        try
        {
            db.CurrentUserId = user.Id;
            KioskGuard.EnsureNotKiosk(kioskContext, db, "Stacking holds");
            await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, ct);
            return db;
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }
}
