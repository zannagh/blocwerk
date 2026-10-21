// <copyright file="HoldColorProposalService.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary><see cref="IHoldColorProposalService"/>: the Wall Settings action and its API route.</summary>
/// <param name="dbContextFactory">Context factory.</param>
/// <param name="currentUserService">The acting user.</param>
/// <param name="proposer">Estimates the colours.</param>
/// <param name="logger">Logger.</param>
/// <param name="journal">Opens the named journal batch, when the host has a journal.</param>
/// <param name="kioskContext">The kiosk context, when the host has one.</param>
public sealed class HoldColorProposalService(
    IDbContextFactory<BlocwerkDbContext> dbContextFactory,
    ICurrentUserService currentUserService,
    IHoldColorProposer proposer,
    ILogger<HoldColorProposalService> logger,
    IChangeJournal? journal = null,
    IKioskContext? kioskContext = null) : IHoldColorProposalService
{
    /// <summary>The journal batch label of an admin run.</summary>
    public const string BatchLabel = "hold-color-proposals";

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
            // Reading the photos takes seconds and changes nothing, so it runs without the wall's hold lock: a panel
            // update that starts meanwhile is not held up. Only the write takes the lock, and it looks at each hold again.
            var plan = await proposer.PlanAsync(db, wallId, ct: ct);
            using var wallLock = WallHoldWriteLock.TryAcquire(
                wallId, "Another update of this wall's holds is running. Try again in a moment.");
            var report = await proposer.ApplyAsync(db, wallId, plan, ct);
            if (report.Any)
            {
                using var scope = journal?.BeginBatch(BatchLabel, ChangeJournalScopeKind.Wall, wallId);
                using var feedScope = FeedScope.Begin(wallId, BatchLabel);
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
