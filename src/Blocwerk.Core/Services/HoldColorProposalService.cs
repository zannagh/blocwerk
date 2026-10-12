// <copyright file="HoldColorProposalService.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary><see cref="IHoldColorProposalService"/>: the Wall Settings action and its API route.</summary>
public sealed class HoldColorProposalService : IHoldColorProposalService
{
    /// <summary>The journal batch label of an admin run.</summary>
    public const string BatchLabel = "hold-color-proposals";

    private readonly IDbContextFactory<BlocwerkDbContext> dbContextFactory;
    private readonly ICurrentUserService currentUserService;
    private readonly IHoldColorProposer proposer;
    private readonly ILogger<HoldColorProposalService> logger;
    private readonly IChangeJournal? journal;
    private readonly IKioskContext? kioskContext;

    /// <summary>Initializes a new instance of the <see cref="HoldColorProposalService"/> class.</summary>
    /// <param name="dbContextFactory">Context factory.</param>
    /// <param name="currentUserService">The acting user.</param>
    /// <param name="proposer">Estimates the colours.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="journal">Opens the named journal batch, when the host has a journal.</param>
    /// <param name="kioskContext">The kiosk context, when the host has one.</param>
    public HoldColorProposalService(
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        ICurrentUserService currentUserService,
        IHoldColorProposer proposer,
        ILogger<HoldColorProposalService> logger,
        IChangeJournal? journal = null,
        IKioskContext? kioskContext = null)
    {
        this.dbContextFactory = dbContextFactory;
        this.currentUserService = currentUserService;
        this.proposer = proposer;
        this.logger = logger;
        this.journal = journal;
        this.kioskContext = kioskContext;
    }

    /// <inheritdoc/>
    public async Task<HoldColorProposalStatus> GetStatusAsync(Guid wallId, CancellationToken ct = default)
    {
        var (db, _) = await OpenForAdminAsync(wallId, ct);
        await using (db)
        {
            var colors = await (await LiveWallHolds.QueryAsync(db, wallId, ct)).AsNoTracking()
                .Where(h => !h.IsVirtual)
                .Select(h => new { h.Color, h.ProposedColor })
                .ToListAsync(ct);
            return new HoldColorProposalStatus(
                colors.Count(c => string.IsNullOrWhiteSpace(c.Color)),
                colors.Count(c => !string.IsNullOrEmpty(c.ProposedColor) && c.ProposedColor == c.Color));
        }
    }

    /// <inheritdoc/>
    public async Task<HoldColorProposalReport> SuggestMissingAsync(Guid wallId, CancellationToken ct = default)
    {
        var (db, userId) = await OpenForAdminAsync(wallId, ct);
        await using (db)
        {
            using var wallLock = WallHoldWriteLock.TryAcquire(
                wallId, "Another update of this wall's holds is running. Try again in a moment.");
            var report = await proposer.ProposeAsync(db, wallId, ct: ct);
            if (report.Any)
            {
                using var scope = journal?.BeginBatch(BatchLabel, ChangeJournalScopeKind.Wall, wallId);
                await db.SaveChangesAsync(ct);
            }

            logger.LogInformation("Colour proposals on wall {WallId} by {UserId}: {Summary}", wallId, userId, report.Summary());
            return report;
        }
    }

    private async Task<(BlocwerkDbContext Db, Guid UserId)> OpenForAdminAsync(Guid wallId, CancellationToken ct)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        var db = await dbContextFactory.CreateDbContextAsync(ct);
        try
        {
            db.CurrentUserId = user.Id;
            KioskGuard.EnsureNotKiosk(kioskContext, db, "Suggesting hold colours");
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
